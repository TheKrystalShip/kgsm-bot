using KGSM.Bot.Core.Common;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Turns an answer into audio.
/// </summary>
/// <remarks>
/// The mirror of <see cref="ISpeechToText"/>, and absent for the same kinds of reason — no model, no
/// card, switched off. A host that cannot speak still answers in the channel, so every caller treats
/// this as an enhancement and never as the way an answer is delivered.
/// </remarks>
public interface ITextToSpeech
{
    /// <summary>Whether there is actually a synthesiser to ask.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Synthesises <paramref name="text"/> as 48 kHz stereo signed 16-bit little-endian PCM — the
    /// format a Discord voice connection is written in — or null when it could not be said.
    /// </summary>
    Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default);

    /// <summary>The voice being spoken in right now. Empty when there is no synthesiser.</summary>
    string SpeakingAs { get; }

    /// <summary>
    /// The voices this host actually has, in the order they are worth choosing from. Empty when there
    /// is no synthesiser.
    /// </summary>
    /// <remarks>
    /// What is <em>installed</em>, read off the disk rather than from a list written down somewhere —
    /// a name offered here and then refused would be the surface lying about its own capabilities.
    /// Answering costs a directory listing and loads nothing, so it is the same answer on a host whose
    /// synthesiser has never been started.
    /// </remarks>
    IReadOnlyList<string> Voices { get; }

    /// <summary>
    /// Speaks in <paramref name="voice"/> from the next sentence on. Fails, changing nothing, when
    /// this host does not have that voice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It costs a name.</b> A voice is a small array of features handed to the synthesiser per
    /// sentence, so this changes which name goes with the next request — no model is reloaded and
    /// nothing is re-warmed. The half-megabyte the new voice takes is read the first time it is
    /// actually spoken in.
    /// </para>
    /// <para>
    /// ⚠ <b>It lasts until the process does.</b> The durable setting is the leaf's own, and this does
    /// not write to it — deliberately, because a bot speaking in a voice its configuration does not
    /// name is two sources of truth, and the one nobody can see wins. It is for hearing a voice before
    /// choosing it; a caller that changes this has to say that it will not survive a restart.
    /// </para>
    /// </remarks>
    Result SpeakAs(string voice);
}
