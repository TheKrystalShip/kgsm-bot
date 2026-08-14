namespace KGSM.Bot.Core.Models;

/// <summary>
/// What a surface wants told to it while a turn is still running.
/// </summary>
/// <remarks>
/// <para>
/// Two consumers because they answer two different questions and a surface wants either, both or
/// neither. <see cref="Steps"/> is what the assistant is <em>consulting</em> — a thread's live
/// account of the work. <see cref="Reply"/> is the answer itself, in the order it is written, for a
/// surface that can begin delivering it before the turn ends.
/// </para>
/// <para>
/// <b>Neither changes the answer.</b> What comes back from the turn is the same reply and the same
/// staged actions either way; asking to watch only decides whether the frames are read instead of the
/// buffered body, and a surface that wants nothing gets the buffered one.
/// </para>
/// </remarks>
/// <param name="Steps">Each tool call as it starts and finishes, or null to be told nothing of them.</param>
/// <param name="Reply">
/// Slices of the reply as they are written — token fragments, not sentences. Called in order, on the
/// thread reading the frames, and nothing waits for it: a slow consumer must not become a slow turn.
/// </param>
public sealed record AssistantStream(
    IProgress<AssistantActivity>? Steps = null,
    IProgress<string>? Reply = null)
{
    /// <summary>Nobody is watching — the buffered answer is all that is wanted.</summary>
    public static readonly AssistantStream None = new();

    /// <summary>Whether anything at all is being watched.</summary>
    public bool Watched => Steps is not null || Reply is not null;
}
