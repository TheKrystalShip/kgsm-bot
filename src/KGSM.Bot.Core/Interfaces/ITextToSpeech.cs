using KGSM.Bot.Core.Common;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Turns an answer into audio.
/// </summary>
/// <remarks>
/// The mirror of <see cref="ISpeechToText"/>, and absent for the same kinds of reason — no speech
/// installed on the host, no model, no card, switched off. A host that cannot speak still answers in
/// the channel, so every caller treats this as an enhancement and never as the way an answer is
/// delivered.
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

    /// <summary>
    /// The voices this host has, in the order they are worth choosing from, and the one it speaks in.
    /// </summary>
    /// <remarks>
    /// <b>A question for the host, not for this process.</b> The voices belong to whatever holds the
    /// models, which serves every surface here — so this is asked rather than remembered, and what
    /// comes back is what is installed rather than what a list claims. Empty is the honest answer on a
    /// host with no speech.
    /// </remarks>
    Task<(string Speaking, IReadOnlyList<string> Voices)> VoicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Speaks in <paramref name="voice"/> from the next sentence on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It changes the voice for the whole host, not just for this bot.</b> One engine serves
    /// every surface, which is what makes a person hear the same assistant in Discord as in a
    /// browser — and means a caller has to say so rather than presenting it as a local preference.
    /// </para>
    /// <para>
    /// <b>It lasts until the engine restarts.</b> The durable setting is that leaf's own; nothing
    /// here writes to it, because a voice its configuration does not name is two sources of truth and
    /// the one nobody can see wins. This is for hearing a voice before choosing it.
    /// </para>
    /// </remarks>
    Task<Result> SpeakAsAsync(string voice, CancellationToken ct = default);
}
