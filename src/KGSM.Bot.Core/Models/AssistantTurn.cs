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
