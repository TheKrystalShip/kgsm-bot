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
}
