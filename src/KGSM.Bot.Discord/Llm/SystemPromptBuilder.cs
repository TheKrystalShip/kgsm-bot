using System.Text;

using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Discord.Llm;

/// <inheritdoc />
public class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IKgsmStateCache _stateCache;
    private readonly ILogger<SystemPromptBuilder> _logger;

    private const string Preamble =
        "You are a friendly assistant in a small Discord server for friends who run game " +
        "servers together. You help them check on and manage their game servers.\n\n" +
        "The lists below are complete and current. When a user asks what servers exist or " +
        "what games can be installed, answer directly from these lists — do NOT call a tool " +
        "for that. When a user refers to a specific server, act directly with the correct " +
        "tool and the exact instance name from the list.\n" +
        "If a request is ambiguous — it could match more than one instance — do NOT guess. " +
        "Ask the user which one they mean and list the candidates.\n" +
        "A single message may ask for several actions in sequence (e.g. stop, then back up, " +
        "then update) — issue the tool calls in the order requested.\n" +
        "Keep replies concise and conversational.";

    private const string ActionsAllowed =
        "\n\nThis user is authorized to perform actions. You can start, stop, restart, back up, " +
        "and update servers, in addition to reading status.\n" +
        "You can also install new servers and uninstall existing ones, but these are " +
        "DESTRUCTIVE: calling install_server or uninstall_server does NOT perform the action — " +
        "it only stages it, and the user is shown a button they must click to confirm. So when " +
        "you use one of those tools, call it once and then tell the user it's awaiting their " +
        "confirmation. NEVER claim a server was installed or uninstalled yourself — you cannot " +
        "complete those; only the confirmation button can.";

    private const string ActionsDenied =
        "\n\nThis user is NOT authorized to perform actions. You can only READ information " +
        "(list servers, status, whether a server is running). If they ask you to start, stop, " +
        "restart, back up, or update a server, politely explain they don't have permission — " +
        "do not attempt it.";

    public SystemPromptBuilder(IKgsmStateCache stateCache, ILogger<SystemPromptBuilder> logger)
    {
        _stateCache = stateCache;
        _logger = logger;
    }

    public async Task<string> BuildAsync(bool canPerformActions, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder(Preamble);
        builder.Append(canPerformActions ? ActionsAllowed : ActionsDenied);

        try
        {
            var instances = await _stateCache.GetInstancesAsync(cancellationToken);
            builder.Append("\n\nCurrently installed instances:\n");
            if (instances.Count > 0)
            {
                foreach (var (name, instance) in instances.OrderBy(kv => kv.Key))
                    builder.Append($"- {name} (game: {instance.Blueprint})\n");
            }
            else
            {
                builder.Append("(none)\n");
            }

            var blueprints = await _stateCache.GetBlueprintsAsync(cancellationToken);
            builder.Append("\nInstallable game types (blueprints): ");
            builder.Append(blueprints.Count > 0
                ? string.Join(", ", blueprints.Keys.OrderBy(k => k))
                : "(none)");
        }
        catch (Exception ex)
        {
            // The model can still operate (and use list tools) without the injected
            // list, so a lookup failure degrades gracefully rather than aborting.
            _logger.LogWarning(ex, "Failed to inject live lists into system prompt");
        }

        return builder.ToString();
    }
}
