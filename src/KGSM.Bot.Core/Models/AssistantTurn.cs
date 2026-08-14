using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Core.Models;

/// <summary>
/// One person's question, as the assistant needs it: who is asking, what authority they hold, which
/// of their conversations it belongs to, and the text itself.
/// </summary>
/// <param name="UserId">The Discord snowflake of the person asking. Their memory is keyed under it.</param>
/// <param name="DisplayName">Their name, for the assistant to address them by.</param>
/// <param name="Tier">
/// The authority their KGSM account holds right now, which decides whether the assistant may propose
/// an action for them or only read.
/// </param>
/// <param name="ConversationId">
/// Which of this person's conversations to continue — the channel they asked in, so a thread in one
/// channel is a separate context window from a thread in another. It sub-scopes their own memory and
/// can never reach anybody else's.
/// </param>
/// <param name="Prompt">What they actually asked, with the bot's mention stripped.</param>
/// <param name="Room">
/// The place this conversation belongs to, when it belongs to a place rather than to the person
/// asking — a thread, where everyone talking to the assistant is talking to it together. Set, it is
/// the conversation: each of them continues the one transcript, and each still acts with their own
/// <paramref name="Tier"/>.
/// <para>
/// It travels alongside <paramref name="ConversationId"/> rather than replacing it, and the assistant
/// prefers it. That is what makes an assistant which knows nothing of rooms degrade cleanly: it reads
/// the conversation id it already understood, and the thread is one person's own context window again
/// — a worse conversation, never a broken one.
/// </para>
/// </param>
/// <param name="Spoken">
/// Whether this answer is going to be read aloud by a synthesiser rather than read on a screen.
/// <para>
/// It describes the <em>surface</em>, not the person and not the question, which is why it travels per
/// turn: this one leaf carries both, and the same account asking the same thing wants a paragraph
/// typed in a text channel and a sentence spoken in a voice one. Speech cannot be skimmed, so on that
/// surface length is duration — a measured 126-character reply was twelve seconds of talking.
/// </para>
/// <para>
/// Presentation only. The assistant's tools, the asker's authority and the propose-then-confirm rule
/// are identical either way: an answer can get shorter and can never get less complete about a staged
/// action. An assistant too old to know the field ignores it and answers as it always did, which is
/// legible when spoken — merely longer than it needs to be.
/// </para>
/// </param>
public sealed record AssistantAsk(
    string UserId,
    string DisplayName,
    KgsmTier Tier,
    string ConversationId,
    string Prompt,
    string? Room = null,
    bool Spoken = false);

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
