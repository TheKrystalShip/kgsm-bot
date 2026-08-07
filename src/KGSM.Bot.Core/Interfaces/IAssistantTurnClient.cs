using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// The bot's line to the kgsm-assistant leaf: it asks a question on a named person's behalf and
/// renders what comes back.
/// </summary>
/// <remarks>
/// <para>
/// The assistant is one service behind every surface, so a conversation held here is the same
/// conversation the Control Panel and the assistant's own site show that person. That is the whole
/// point of reaching it over the wire rather than running a second one in this process: two engines
/// mean two memories, and a person asking "what did I ask you yesterday?" gets a different answer
/// depending on which one they happen to be talking to.
/// </para>
/// <para>
/// The bot forwards <em>the asking human</em>, never a service account. Authority is resolved here,
/// from the roles Discord hands the bot, and travels with the question — so the assistant can only
/// ever act with the permissions of whoever asked.
/// </para>
/// </remarks>
public interface IAssistantTurnClient
{
    /// <summary>
    /// Whether this host has an assistant to reach at all. False leaves the conversational surface
    /// unavailable; it says nothing about whether the assistant is up right now.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether the assistant is answering right now. A liveness check only — it is asked so the bot
    /// can say "the assistant is unreachable" instead of leaving someone waiting, never so the bot
    /// can answer in its place.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Puts one question to the assistant on <paramref name="ask"/>'s author's behalf.
    /// </summary>
    /// <returns>
    /// The reply and anything it staged, or a failure carrying text fit to show the person who
    /// asked — an unreachable assistant is reported, never papered over with a local answer.
    /// </returns>
    Task<Result<AssistantTurn>> AskAsync(AssistantAsk ask, CancellationToken ct = default);

    /// <summary>
    /// Approves a staged action on behalf of the person who clicked, redeeming the grant the
    /// assistant issued when it proposed the action.
    /// </summary>
    /// <remarks>
    /// The bot holds nothing of the action itself — only the grant, which it carries on the button
    /// and hands straight back. Deciding whether the click may proceed is the assistant's: it
    /// re-derives the approver's authority, re-validates the target against what exists now, and
    /// refuses a grant belonging to somebody else. The bot's own tier check in front of this is a
    /// courtesy to the clicker, never the gate.
    /// </remarks>
    /// <returns>
    /// What became of the action, or a failure carrying text fit to show — a grant that has expired
    /// or already been used is reported as such, never retried and never assumed to have run.
    /// </returns>
    Task<Result<AssistantOutcome>> ConfirmAsync(AssistantApproval approval, CancellationToken ct = default);
}
