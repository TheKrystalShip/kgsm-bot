using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord;

/// <summary>
/// Listens for messages that @-mention the bot and puts them to the assistant, which handles
/// conversation memory and tool calls. This class owns only the Discord I/O concerns: trigger
/// detection, typing, and replying.
/// </summary>
/// <remarks>
/// The answer comes from the kgsm-assistant leaf and nowhere else. There is no second engine in
/// this process to fall back to, deliberately: a fallback would split one person's history across
/// two memories precisely when things are going wrong, and neither would hold the whole
/// conversation. An unreachable or unconfigured assistant means this surface says so and goes quiet
/// — slash commands, announcements and channel status are untouched.
/// </remarks>
public class MessageHandler
{
    // Discord hard-caps a single message at 2000 characters.
    private const int DiscordMessageLimit = 2000;

    private readonly DiscordSocketClient _client;
    private readonly IAssistantTurnClient _assistant;
    private readonly IKgsmAccounts _accounts;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(
        DiscordSocketClient client,
        IAssistantTurnClient assistant,
        IKgsmAccounts accounts,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _assistant = assistant;
        _accounts = accounts;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _client.MessageReceived += OnMessageReceivedAsync;

        // Say at startup whether the assistant is answering, where an operator will see it. The
        // alternative is learning it from the first person who asks a question and gets an apology.
        if (_assistant.IsConfigured)
            _logger.LogInformation(
                "Conversational surface: the kgsm-assistant leaf, reachable={Reachable}",
                await _assistant.IsAvailableAsync());
        else
            _logger.LogWarning(
                "No assistant is configured (Assistant:BaseUrl / Assistant:RelaySecret) — the bot " +
                "will not answer @-mentions. Slash commands and announcements are unaffected.");

        _logger.LogInformation("Message handler initialized");
    }

    private Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        // Offload to a worker so a slow LLM call doesn't block the gateway event
        // loop (Discord.Net awaits handlers sequentially). HandleAsync owns its
        // own try/catch, so nothing is silently dropped.
        _ = Task.Run(() => HandleAsync(rawMessage));
        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketMessage rawMessage)
    {
        try
        {
            // Only handle real user messages (ignore system messages, embeds-only, etc.)
            if (rawMessage is not SocketUserMessage message)
                return;

            // Never respond to bots or to ourselves (prevents loops).
            if (message.Author.IsBot || message.Author.Id == _client.CurrentUser.Id)
                return;

            // Trigger: the bot must be explicitly @-mentioned.
            var mentioned = message.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);
            if (!mentioned)
                return;

            var prompt = StripBotMention(message.Content);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                await message.ReplyAsync("👋 You mentioned me — what can I help with?");
                return;
            }

            if (!_assistant.IsConfigured)
            {
                // Said once per message rather than logged and ignored: someone asked a question and
                // is owed an answer, even when the answer is that nobody is home.
                await message.ReplyAsync(
                    "💤 There's no assistant set up on this host, so I can't answer questions here — " +
                    "the slash commands still work.");
                return;
            }

            // The authority the author holds right now, from the KGSM account their Discord account
            // is connected to — the same record the Control Panel and the assistant read. Asking a
            // question needs an account here; acting through one needs operator.
            AccountAnswer account = await _accounts.ResolveAsync(message.Author.Id);
            if (!account.Allows(KgsmTier.Viewer))
            {
                // Answered rather than ignored: somebody asked a question, and being told why there
                // is no answer is worth more than silence they cannot interpret.
                await message.ReplyAsync(account.Refusal(KgsmTier.Viewer));
                return;
            }

            _logger.LogDebug(
                "Assistant prompt from {User} ({UserId}, account={Account}, tier={Tier}): {Prompt}",
                message.Author.Username, message.Author.Id, account.Account, account.Tier, prompt);

            await AnswerAsync(message, prompt, account.Tier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
        }
    }

    /// <summary>
    /// Puts the question to the kgsm-assistant leaf on the author's behalf, and posts what comes back.
    /// </summary>
    /// <remarks>
    /// No provenance scope is opened here: this turn's tool calls run in the assistant's process,
    /// which stamps them from the identity and the leaf name the relay carries. Nothing on this path
    /// reaches kgsm from inside the bot, so an ambient actor set here would attribute nothing.
    /// </remarks>
    private async Task AnswerAsync(SocketUserMessage message, string prompt, KgsmTier tier)
    {
        Result<AssistantTurn> result;
        using (message.Channel.EnterTypingState())
        {
            result = await _assistant.AskAsync(new AssistantAsk(
                message.Author.Id.ToString(),
                message.Author.Username,
                tier,
                // The channel is the conversation. Each channel is its own context window, and the
                // thread is the same one this person sees wherever else they reach the assistant.
                message.Channel.Id.ToString(),
                prompt,
                RoomFor(message.Channel)));
        }

        if (result.IsFailure)
        {
            await message.ReplyAsync($"⚠️ {result.Error}");
            return;
        }

        var turn = result.Value!;
        if (!string.IsNullOrWhiteSpace(turn.Text))
            await message.ReplyAsync(Truncate(turn.Text, DiscordMessageLimit));
        else if (turn.StagedActions.Count == 0)
            await message.ReplyAsync("🤔 I didn't have anything to say to that.");

        foreach (var staged in turn.StagedActions)
            await PostStagedActionAsync(message, staged);
    }

    /// <summary>
    /// The shared conversation this message belongs to, or <see langword="null"/> when it belongs to
    /// whoever sent it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A thread is a room; a channel is not.</b> People in a thread are having one conversation and
    /// expect the assistant to be in it — answering the fourth question as though the first three
    /// happened is the whole point of talking in a thread rather than in the channel. A channel has no
    /// such boundary: it is a room nobody joined and nobody leaves, where an hour-old exchange between
    /// two other people is context this conversation never had.
    /// </para>
    /// <para>
    /// Named by guild AND thread, because a thread id is unique to Discord but the room key is not
    /// Discord's alone — the guild segment keeps two hosts' threads apart in a store that holds both.
    /// </para>
    /// </remarks>
    private static string? RoomFor(ISocketMessageChannel channel) =>
        channel is SocketThreadChannel thread ? $"{thread.Guild.Id}-{thread.Id}" : null;

    /// <summary>
    /// Posts a prompt for an action the assistant staged, with Confirm/Cancel buttons.
    /// </summary>
    /// <remarks>
    /// The button carries the assistant's grant and nothing else, so the bot stores no part of the
    /// pending action and a restart of either side leaves a posted button working. Only the person
    /// who asked can approve it — <see cref="Commands.AssistantConfirmationModule"/> forwards the
    /// clicker and the assistant refuses a grant that is not theirs.
    /// </remarks>
    private static async Task PostStagedActionAsync(SocketUserMessage message, StagedAction staged)
    {
        if (!AssistantConfirmationIds.Fits(staged.Token))
        {
            // Nothing to salvage: a button built from a grant that does not fit is one Discord
            // accepts and the assistant then refuses, which is worse than saying so now.
            await message.ReplyAsync(
                $"⚠️ I staged *{Describe(staged)}* but couldn't build a button for it — " +
                "please use the slash command instead.");
            return;
        }

        // Uninstalling is the one that cannot be undone, so it is the one that gets the alarming
        // button; everything else is recoverable and gets a neutral one.
        var style = staged.Kind == "uninstall" ? ButtonStyle.Danger : ButtonStyle.Primary;

        var components = new ComponentBuilder()
            .WithButton("Confirm", AssistantConfirmationIds.Confirm(staged.Token), style)
            .WithButton("Cancel", AssistantConfirmationIds.Cancel, ButtonStyle.Secondary)
            .Build();

        await message.ReplyAsync(StagedActionContent(staged), components: components);
    }

    private static string StagedActionContent(StagedAction staged) => staged.Kind switch
    {
        "uninstall" =>
            $"⚠️ This will **permanently delete `{staged.Target}`** and all of its data. This cannot be undone.",
        "install" =>
            $"⚙️ This will install a new **{staged.Target}** server" +
            (staged.InstanceName is null ? "" : $" named `{staged.InstanceName}`") +
            ". It can take a while.",
        "start" => $"▶️ Start **{staged.Target}**?",
        "stop" => $"⏹️ Stop **{staged.Target}**?",
        "restart" => $"🔄 Restart **{staged.Target}**?",
        "update" => $"⬆️ Update **{staged.Target}** to its latest version? It can take a while.",
        "backup" => $"💾 Back up **{staged.Target}**?",
        "setconfig" =>
            $"⚙️ Set `{staged.ConfigKey}` = `{(string.IsNullOrEmpty(staged.ConfigValue) ? "(empty)" : staged.ConfigValue)}` " +
            $"on **{staged.Target}**?",
        // A kind this bot has no wording for is still a real staged action, so it is offered rather
        // than dropped — the assistant's own reply above says what it is.
        _ => $"Confirm *{Describe(staged)}*?",
    };

    private static string Describe(StagedAction staged) => staged.Kind switch
    {
        "setconfig" => $"set `{staged.ConfigKey}` on **{staged.Target}**",
        "install" => $"install **{staged.Target}**"
            + (staged.InstanceName is null ? "" : $" as `{staged.InstanceName}`"),
        _ => $"{staged.Kind} **{staged.Target}**",
    };

    /// <summary>
    /// Removes the leading/inline mention of the bot (both &lt;@id&gt; and &lt;@!id&gt; forms)
    /// so the model sees a clean prompt.
    /// </summary>
    private string StripBotMention(string content)
    {
        var id = _client.CurrentUser.Id;
        var pattern = $"<@!?{id}>";
        return Regex.Replace(content, pattern, string.Empty).Trim();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
