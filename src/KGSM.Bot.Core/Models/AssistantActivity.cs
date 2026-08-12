namespace KGSM.Bot.Core.Models;

/// <summary>What a step of a turn is doing.</summary>
public enum AssistantActivityState
{
    /// <summary>Under way — the tool was called and has not come back.</summary>
    Running,

    /// <summary>Finished, and its result is part of the answer.</summary>
    Done,

    /// <summary>Finished without producing anything usable.</summary>
    Failed,
}

/// <summary>
/// One step of the assistant's work on a turn, as a surface can show it: what it consulted, what
/// about, and where that step got to.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it looked at, never what it read.</b> <see cref="Detail"/> describes a result — how many
/// lines, how many events, what verdict — and never carries the tool's own output. A tool's output is
/// the model's grounding text: a console read returns the server's log, which routinely carries the
/// network address of everyone who connected. This bot already refuses to put an address in a roster
/// and keeps <c>/logs</c> out of the channel for exactly that reason, and a thread is more public
/// than either.
/// </para>
/// <para>
/// Surface-neutral by design: the activity says what happened, and each surface decides how much of
/// it is worth a message.
/// </para>
/// </remarks>
/// <param name="Id">
/// The assistant's id for this step, pairing a finish with the start it belongs to. A surface keyed
/// on the tool name alone would collapse two reads of two different servers into one line.
/// </param>
/// <param name="Tool">The tool's own name, as the assistant calls it (<c>read_console</c>).</param>
/// <param name="Label">That tool in words, for somebody who does not know the catalog.</param>
/// <param name="Subject">What the step is about — usually a server — or null when it names nothing.</param>
/// <param name="Detail">
/// A short description of the result, safe to show. Null while the step is still running, and null
/// for a result there is nothing brief to say about.
/// </param>
/// <param name="State">Where the step got to.</param>
public sealed record AssistantActivity(
    string Id,
    string Tool,
    string Label,
    string? Subject,
    string? Detail,
    AssistantActivityState State);
