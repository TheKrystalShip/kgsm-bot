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
    /// Runs one of the assistant's own chat commands — <c>compact</c>, <c>new</c> — against the
    /// conversation <paramref name="ask"/> names, and returns what the assistant said about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a question, and not answered by the model.</b> These act on the conversation itself, so
    /// they go to the endpoint that owns it rather than being phrased as something to ask — a model
    /// told to forget the conversation would reply that it had, and remember every word.
    /// </para>
    /// <para>
    /// <b>The assistant decides what each one means and who may run it</b>, including refusing. This
    /// carries a name and gets a sentence back; there is no second list of commands here to fall out of
    /// step with the one the assistant publishes.
    /// </para>
    /// </remarks>
    Task<Result<string>> RunCommandAsync(string command, AssistantAsk ask, CancellationToken ct = default);

    /// <summary>
    /// Puts one question, reporting each step of the work as it happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same question and the same answer as <see cref="AskAsync"/>; what differs is that the
    /// caller is told what the assistant is consulting while it is still consulting it. A turn that
    /// reads a console, runs a health check and searches the guides takes as long as those take, and
    /// a surface with nothing to show for that time is one where people cannot tell work from a hang.
    /// </para>
    /// <para>
    /// <paramref name="activity"/> is called on a background thread as frames arrive, in order, and
    /// may be called many times for one step. A surface that cannot keep up must coalesce; nothing
    /// here waits for it, because a slow renderer must not become a slow turn.
    /// </para>
    /// <para>
    /// <b>Activities describe, never quote.</b> What a tool returned is the model's grounding text —
    /// a console read is the server's log — and this surface does not publish it. See
    /// <see cref="AssistantActivity"/>.
    /// </para>
    /// </remarks>
    Task<Result<AssistantTurn>> AskAsync(
        AssistantAsk ask, IProgress<AssistantActivity>? activity, CancellationToken ct = default);

    /// <summary>
    /// Puts one question, telling <paramref name="stream"/>'s consumers what is happening while it is
    /// happening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The general form of the two overloads above: a surface says which of the work and the reply it
    /// wants to hear about, and gets the same answer either way. Watching nothing takes the buffered
    /// body, which is what a surface that only posts a finished answer needs.
    /// </para>
    /// <para>
    /// <b>The reply arrives as it is written, in slices that are not sentences.</b> A surface that
    /// delivers as it goes — speaking a voice channel's answer sentence by sentence — owns the job of
    /// finding the boundaries in it — <c>TheKrystalShip.KGSM.Speech.SpokenSentences</c>, which every
    /// surface on this host that speaks reads the same rules out of.
    /// </para>
    /// </remarks>
    Task<Result<AssistantTurn>> AskAsync(
        AssistantAsk ask, AssistantStream stream, CancellationToken ct = default);

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
