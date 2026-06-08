using Microsoft.Extensions.Logging;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// Assembles a kgsm-policy-bearing <see cref="AgentTurn"/> and runs it through the
/// reusable library agent loop (<see cref="ILlmAgent"/>).
///
/// This is where ALL kgsm authorization policy lives:
///  - which tools are offered (read-only for everyone, mutating/destructive only for authorized callers);
///  - the per-message blast-radius cap on mutating actions;
///  - the defense-in-depth refusal of a mutating/destructive call from an unauthorized caller;
///  - draining the destructive ops the dispatcher staged this turn so the caller can confirm them.
/// The library loop knows none of this — it just evaluates the gate we supply.
/// </summary>
public class ServerAssistant : IServerAssistant
{
    /// <summary>Blast-radius limit: at most this many mutating actions per user message.</summary>
    private const int MaxActionsPerMessage = 5;

    /// <summary>
    /// Blast-radius limit on the DESTRUCTIVE tier: at most this many install/uninstall
    /// ops may be staged (proposed) per user message. These never execute without a
    /// per-op human button-click, but this still stops one prompt from teeing up a
    /// library-wide shuffle. Tunable; kept small on purpose.
    /// </summary>
    private const int MaxDestructiveStagedPerMessage = 3;

    private readonly ILlmAgent _agent;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IConfirmationContext _confirmations;
    private readonly ILogger<ServerAssistant> _logger;

    public ServerAssistant(
        ILlmAgent agent,
        ISystemPromptBuilder promptBuilder,
        IConfirmationContext confirmations,
        ILogger<ServerAssistant> logger)
    {
        _agent = agent;
        _promptBuilder = promptBuilder;
        _confirmations = confirmations;
        _logger = logger;
    }

    public async Task<AssistantResult> RunAsync(
        ulong userId,
        ulong channelId,
        string userPrompt,
        bool canPerformActions,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _promptBuilder.BuildAsync(canPerformActions, cancellationToken);

        // Mutating + destructive tools are only offered to authorized callers; the gate re-checks.
        var tools = canPerformActions ? LlmTools.All : LlmTools.ReadOnly;

        var turn = new AgentTurn
        {
            ConversationId = ConversationId(userId, channelId),
            UserPrompt = userPrompt,
            SystemPrompt = systemPrompt,
            Tools = tools,
            Gate = BuildGate(canPerformActions),
        };

        // The dispatcher stages any destructive ops into this per-turn scope; we drain
        // them after the run so the caller can post confirmation prompts.
        using (_confirmations.BeginTurn())
        {
            var result = await _agent.RunAsync(turn, cancellationToken);
            var confirmations = _confirmations.Staged;

            return result.IsSuccess
                ? AssistantResult.Ok(result.Value!, confirmations)
                : AssistantResult.Fail(result.Error!);
        }
    }

    /// <summary>
    /// Per-turn gate closure.
    ///  - Read-only tools always pass.
    ///  - Mutating tools: refused for unauthorized callers; capped at <see cref="MaxActionsPerMessage"/>.
    ///  - Destructive tools: refused for unauthorized callers; otherwise allowed through to the
    ///    dispatcher, which only STAGES them (it never executes), so they don't consume the cap.
    /// The closure holds the per-message mutating-action counter.
    /// </summary>
    private static Func<LlmToolCall, ToolGate> BuildGate(bool canPerformActions)
    {
        var actionsTaken = 0;
        var destructiveStaged = 0;
        return call =>
        {
            if (LlmTools.IsDestructive(call.Name))
            {
                if (!canPerformActions)
                    return ToolGate.Refuse("Refused: you don't have permission to perform server actions.");

                if (destructiveStaged >= MaxDestructiveStagedPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxDestructiveStagedPerMessage} install/uninstall actions " +
                        "can be proposed per message. Ask the user to do these one at a time.");

                destructiveStaged++;
                return ToolGate.Allow;
            }

            if (!LlmTools.IsMutating(call.Name))
                return ToolGate.Allow;

            if (!canPerformActions)
                return ToolGate.Refuse(
                    "Refused: you don't have permission to perform server actions.");

            if (actionsTaken >= MaxActionsPerMessage)
                return ToolGate.Refuse(
                    $"Refused: the limit of {MaxActionsPerMessage} actions per message has been " +
                    "reached. Ask the user to send the remaining actions separately.");

            actionsTaken++;
            return ToolGate.Allow;
        };
    }

    private static string ConversationId(ulong userId, ulong channelId) => $"{userId}:{channelId}";
}
