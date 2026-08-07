using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Discord.Llm;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord;

/// <summary>
/// Listens for messages that @-mention the bot and puts them to the assistant, which handles
/// conversation memory and tool calls. This class owns only the Discord I/O concerns: trigger
/// detection, typing, and replying.
/// </summary>
/// <remarks>
/// Where the answer comes from is decided once, at startup, by whether this host configured an
/// assistant to reach (<see cref="IAssistantTurnClient.IsConfigured"/>). It is never a per-message
/// choice: a bot that fell back to a second engine when the first was unreachable would split one
/// person's history across two memories precisely when things are going wrong, and neither would
/// hold the whole conversation. Configured, an unreachable assistant means the conversational
/// surface says so and goes quiet — slash commands, announcements and channel status are untouched.
/// </remarks>
public class MessageHandler
{
    // Discord hard-caps a single message at 2000 characters.
    private const int DiscordMessageLimit = 2000;

    private readonly DiscordSocketClient _client;
    private readonly IServerAssistant _assistant;
    private readonly IAssistantTurnClient _assistantClient;
    private readonly KgsmRoleMap _roleMap;
    private readonly PendingEditStore _pendingEdits;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(
        DiscordSocketClient client,
        IServerAssistant assistant,
        IAssistantTurnClient assistantClient,
        KgsmRoleMap roleMap,
        PendingEditStore pendingEdits,
        IInvocationContext invocation,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _assistant = assistant;
        _assistantClient = assistantClient;
        _roleMap = roleMap;
        _pendingEdits = pendingEdits;
        _invocation = invocation;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _client.MessageReceived += OnMessageReceivedAsync;

        // Say at startup whether the assistant is answering, where an operator will see it. The
        // alternative is learning it from the first person who asks a question and gets an apology.
        if (_assistantClient.IsConfigured)
            _logger.LogInformation(
                "Conversational surface: the kgsm-assistant leaf, reachable={Reachable}",
                await _assistantClient.IsAvailableAsync());

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

            // Authorization for mutating actions: the author must hold operator on this host, from
            // the same role map the Control Panel and the assistant answer with. Reading stays open to
            // any guild member. A host that configured no role ids leaves everyone at viewer, so
            // nothing acts until the roles are set.
            // The authority the author holds right now, from the roles the gateway already handed us.
            // Reading is open to any guild member; acting needs operator.
            var tier = _roleMap.ResolveSnowflakes(
                (message.Author as SocketGuildUser)?.Roles.Select(r => r.Id));
            var canPerformActions = tier >= KgsmTier.Operator;

            _logger.LogDebug(
                "LLM prompt from {User} ({UserId}, tier={Tier}): {Prompt}",
                message.Author.Username, message.Author.Id, tier, prompt);

            if (_assistantClient.IsConfigured)
                await AnswerFromAssistantAsync(message, prompt, tier);
            else
                await AnswerLocallyAsync(message, prompt, canPerformActions);
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
    private async Task AnswerFromAssistantAsync(SocketUserMessage message, string prompt, KgsmTier tier)
    {
        Result<AssistantTurn> result;
        using (message.Channel.EnterTypingState())
        {
            result = await _assistantClient.AskAsync(new AssistantAsk(
                message.Author.Id.ToString(),
                message.Author.Username,
                tier,
                // The channel is the conversation. Each channel is its own context window, and the
                // thread is the same one this person sees wherever else they reach the assistant.
                message.Channel.Id.ToString(),
                prompt));
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
    /// Answers from the agent loop running inside this process, on a host with no assistant leaf.
    /// </summary>
    private async Task AnswerLocallyAsync(SocketUserMessage message, string prompt, bool canPerformActions)
    {
        AssistantResult result;
        // Attribute any server mutation the LLM runs this turn to the asking Discord user
        // (origin=discord). Flows down the awaited RunAsync → tool dispatch → kgsm chokepoint.
        // (Destructive ops are usually staged for a confirmation click — re-attributed there — but
        // wrapping the turn covers any inline execution too.)
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(message.Author.Username));
        using (message.Channel.EnterTypingState())
        {
            // Conversation key for memory: per (user, channel). The assistant
            // treats this as an opaque id (a web client would supply its own).
            result = await _assistant.RunAsync(
                $"{message.Author.Id}:{message.Channel.Id}", prompt, canPerformActions);
        }

        if (result.IsFailure)
        {
            _logger.LogWarning("Agent run failed: {Error}", result.Error);
            await message.ReplyAsync($"⚠️ {result.Error}");
            return;
        }

        var reply = result.Text;
        if (!string.IsNullOrWhiteSpace(reply))
            await message.ReplyAsync(Truncate(reply, DiscordMessageLimit));
        else if (result.Confirmations.Count == 0)
            await message.ReplyAsync("🤔 I didn't have anything to say to that.");

        // Each staged destructive op gets its own confirmation prompt with buttons.
        // Nothing runs until a permitted human clicks Confirm (see ConfirmationModule).
        foreach (var confirmation in result.Confirmations)
            await PostConfirmationAsync(message, confirmation);
    }

    /// <summary>
    /// Reports an action the assistant staged, and points at the command that performs it.
    /// </summary>
    /// <remarks>
    /// A click is not carried back to the assistant, so the grant it issued cannot be redeemed from
    /// here and no button is offered. Naming the action is what keeps the reply honest: the assistant
    /// has said it will do something, and the person reading needs to know it has not.
    /// </remarks>
    private static async Task PostStagedActionAsync(SocketUserMessage message, StagedAction staged)
    {
        var what = staged.Kind switch
        {
            "setconfig" => $"set `{staged.ConfigKey}` on **{staged.Target}**",
            "install" => $"install **{staged.Target}**"
                + (staged.InstanceName is null ? "" : $" as `{staged.InstanceName}`"),
            _ => $"{staged.Kind} **{staged.Target}**",
        };

        await message.ReplyAsync(
            $"⚠️ I staged *{what}*, but confirming an action from a chat message isn't something " +
            "I can carry through — use the slash command for it.");
    }

    /// <summary>
    /// Posts a confirmation prompt for a staged destructive op, with Confirm/Cancel
    /// buttons. Execution happens only on a Confirm click, handled (and re-authorized)
    /// by <see cref="Commands.ConfirmationModule"/>.
    /// </summary>
    private async Task PostConfirmationAsync(SocketUserMessage message, PendingConfirmation confirmation)
    {
        var confirmId = ConfirmationIds.Confirm(confirmation);
        if (confirmId.Length > ConfirmationIds.MaxCustomIdLength)
        {
            // A SetConfig value can legitimately be long (e.g. executable_arguments), which
            // overflows Discord's 100-char customId. Stash the resolved op server-side and
            // ride a short id instead, so the button still works. Other kinds can only
            // overflow on an absurd instance name → keep the existing guidance.
            if (confirmation.Kind == ConfirmationKind.SetConfig)
            {
                confirmId = ConfirmationIds.ConfirmStored(_pendingEdits.Stash(confirmation));
            }
            else
            {
                await message.ReplyAsync(
                    "⚠️ That name is too long for me to build a confirmation button — please use the slash command instead.");
                return;
            }
        }

        // Destructive ops get the alarming red button; ordinary commands a neutral one.
        var confirmStyle = ConfirmationKinds.IsDestructive(confirmation.Kind)
            ? ButtonStyle.Danger
            : ButtonStyle.Primary;

        var components = new ComponentBuilder()
            .WithButton("Confirm", confirmId, confirmStyle)
            .WithButton("Cancel", ConfirmationIds.Cancel, ButtonStyle.Secondary)
            .Build();

        await message.ReplyAsync(ConfirmationContent(confirmation), components: components);
    }

    private static string ConfirmationContent(PendingConfirmation c) => c.Kind switch
    {
        ConfirmationKind.Uninstall =>
            $"⚠️ This will **permanently delete `{c.Target}`** and all of its data. This cannot be undone.",
        ConfirmationKind.Install =>
            $"⚙️ This will install a new **{c.Target}** server" +
            (c.InstanceName is null ? "" : $" named `{c.InstanceName}`") +
            ". It can take a while.",
        ConfirmationKind.Start => $"▶️ Start **{c.Target}**?",
        ConfirmationKind.Stop => $"⏹️ Stop **{c.Target}**?",
        ConfirmationKind.Restart => $"🔄 Restart **{c.Target}**?",
        ConfirmationKind.Update => $"⬆️ Update **{c.Target}** to its latest version? It can take a while.",
        ConfirmationKind.Backup => $"💾 Back up **{c.Target}**?",
        ConfirmationKind.SetConfig =>
            $"⚙️ Set `{c.ConfigKey}` = `{(string.IsNullOrEmpty(c.ConfigValue) ? "(empty)" : c.ConfigValue)}` " +
            $"on **{c.Target}**?",
        _ => "Please confirm this action."
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
