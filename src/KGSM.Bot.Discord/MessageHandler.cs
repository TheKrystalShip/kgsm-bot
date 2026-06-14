using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

using KGSM.Bot.Discord.Llm;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;

namespace KGSM.Bot.Discord;

/// <summary>
/// Listens for messages that @-mention the bot and routes them through the LLM
/// agent (which handles conversation memory and tool calls). This class owns
/// only the Discord I/O concerns: trigger detection, typing, and replying.
/// </summary>
public class MessageHandler
{
    // Discord hard-caps a single message at 2000 characters.
    private const int DiscordMessageLimit = 2000;

    private readonly DiscordSocketClient _client;
    private readonly IServerAssistant _assistant;
    private readonly DiscordOptions _discordOptions;
    private readonly PendingEditStore _pendingEdits;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(
        DiscordSocketClient client,
        IServerAssistant assistant,
        IOptions<DiscordOptions> discordOptions,
        PendingEditStore pendingEdits,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _assistant = assistant;
        _discordOptions = discordOptions.Value;
        _pendingEdits = pendingEdits;
        _logger = logger;
    }

    public Task InitializeAsync()
    {
        _client.MessageReceived += OnMessageReceivedAsync;
        _logger.LogInformation("Message handler initialized");
        return Task.CompletedTask;
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

            // Authorization for mutating actions: the author must hold the configured role.
            // Read-only stays open to everyone. If no role is configured, no one is authorized.
            var canPerformActions =
                _discordOptions.ActionRoleId != 0 &&
                message.Author is SocketGuildUser guildUser &&
                guildUser.Roles.Any(r => r.Id == _discordOptions.ActionRoleId);

            _logger.LogDebug(
                "LLM prompt from {User} ({UserId}, canAct={CanAct}): {Prompt}",
                message.Author.Username, message.Author.Id, canPerformActions, prompt);

            AssistantResult result;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
        }
    }

    /// <summary>
    /// Posts a confirmation prompt for a staged destructive op, with Confirm/Cancel
    /// buttons. Execution happens only on a Confirm click, handled (and re-authorized)
    /// by <see cref="ConfirmationModule"/>.
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
