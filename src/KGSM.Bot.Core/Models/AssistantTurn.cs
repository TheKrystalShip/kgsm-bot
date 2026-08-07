using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Core.Models;

/// <summary>
/// One person's question, as the assistant needs it: who is asking, what authority they hold, which
/// of their conversations it belongs to, and the text itself.
/// </summary>
/// <param name="UserId">The Discord snowflake of the person asking. Their memory is keyed under it.</param>
/// <param name="DisplayName">Their name, for the assistant to address them by.</param>
/// <param name="Tier">
/// The authority resolved from the roles they hold right now, which decides whether the assistant may
/// propose an action for them or only read.
/// </param>
/// <param name="ConversationId">
/// Which of this person's conversations to continue — the channel they asked in, so a thread in one
/// channel is a separate context window from a thread in another. It sub-scopes their own memory and
/// can never reach anybody else's.
/// </param>
/// <param name="Prompt">What they actually asked, with the bot's mention stripped.</param>
public sealed record AssistantAsk(
    string UserId,
    string DisplayName,
    KgsmTier Tier,
    string ConversationId,
    string Prompt);

/// <summary>
/// What the assistant answered: the reply to show, and any action it staged for a human to approve.
/// </summary>
public sealed record AssistantTurn(string Text, IReadOnlyList<StagedAction> StagedActions);

/// <summary>
/// An action the assistant proposed but did not take. Nothing happens until a permitted person
/// confirms it, which is what the <see cref="Token"/> is for — an opaque, expiring grant the
/// assistant issued and will only accept back from someone still authorized.
/// </summary>
/// <param name="Kind">The operation, lower-cased (<c>start</c>, <c>uninstall</c>, <c>setconfig</c>…).</param>
/// <param name="Target">What it acts on — an instance name, or a blueprint for an install.</param>
/// <param name="InstanceName">The name a new server would be installed under, when one was asked for.</param>
/// <param name="Token">The assistant's opaque grant. Never parsed here; handed back verbatim.</param>
/// <param name="ConfigKey">Set only for a configuration change.</param>
/// <param name="ConfigValue">Set only for a configuration change.</param>
public sealed record StagedAction(
    string Kind,
    string Target,
    string? InstanceName,
    string Token,
    string? ConfigKey = null,
    string? ConfigValue = null);

/// <summary>
/// One person approving one staged action: who is approving, what authority they hold at the moment
/// they approve, and the grant they are redeeming.
/// </summary>
/// <remarks>
/// The approver is named separately from whoever staged it because approving is its own act, judged
/// on its own. The assistant redeems a grant only for the person it was staged for, so the two turn
/// out to be the same person — but this surface forwards who actually clicked, and lets the authority
/// that issued the grant decide whether that is allowed.
/// </remarks>
public sealed record AssistantApproval(
    string UserId,
    string DisplayName,
    KgsmTier Tier,
    string Token);

/// <summary>
/// What became of an approved action.
/// </summary>
/// <param name="Text">The outcome as the assistant tells it, fit to show the person who approved.</param>
/// <param name="Success">
/// Whether the action may be reported as having succeeded — true only when the end state was
/// observed, or when the engine reported success for something with no end state to observe.
/// </param>
/// <param name="Verdict">
/// Which of those it was: <c>settled</c> (observed), <c>accepted</c> (reported, nothing to observe),
/// <c>notSettled</c> (ran without arriving), <c>unknown</c> (the end state could not be read).
/// </param>
/// <param name="ObservedState">
/// The run state measured afterwards, for the verbs that have one. <c>unknown</c> means the read
/// failed and is never a stand-in for "not running".
/// </param>
public sealed record AssistantOutcome(
    string Text,
    bool Success,
    string? Verdict = null,
    string? ObservedState = null);
