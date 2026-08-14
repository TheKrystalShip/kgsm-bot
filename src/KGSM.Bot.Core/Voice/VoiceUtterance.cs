namespace KGSM.Bot.Core.Voice;

/// <summary>
/// One continuous stretch of one person talking, as 16 kHz mono signed 16-bit PCM.
/// </summary>
/// <remarks>
/// <para>
/// The speaker is a Discord account id because that is what the voice connection hands over —
/// received audio arrives on a stream keyed by user, so who said something is measured rather than
/// inferred. It is <em>not</em> authority: what that account may ask this host to do is the KGSM
/// account it is connected to, resolved the same way every other surface resolves it.
/// </para>
/// <para>
/// <see cref="Duration"/> is derived from how many bytes there are, never from the clock. Frames
/// arrive only while somebody is talking, so wall-clock time between the first and last frame counts
/// the pauses too and would describe a two-second sentence spoken across a ten-second stretch as ten
/// seconds of audio.
/// </para>
/// </remarks>
/// <param name="SpeakerId">The Discord account the audio came from.</param>
/// <param name="SpeakerName">That account's display name, for logs and transcripts.</param>
/// <param name="Audio">16 kHz mono signed 16-bit little-endian PCM.</param>
/// <param name="Duration">How much audio <paramref name="Audio"/> holds.</param>
/// <param name="StartedAt">When the first frame of it arrived.</param>
public sealed record VoiceUtterance(
    ulong SpeakerId,
    string SpeakerName,
    byte[] Audio,
    TimeSpan Duration,
    DateTimeOffset StartedAt);

/// <summary>
/// The three durations that decide where one utterance ends and the next begins.
/// </summary>
/// <param name="SilenceGap">
/// How long a speaker has to stop producing audio before what they said is treated as finished.
/// </param>
/// <param name="MinDuration">
/// The shortest thing worth passing on. Below this it is a cough, a keyboard, or the tail of
/// somebody else's sentence bleeding through.
/// </param>
/// <param name="MaxDuration">
/// The longest an utterance may run before it is cut and handed over regardless. Somebody who talks
/// without pausing is not an error, but an unbounded buffer is.
/// </param>
public sealed record UtteranceLimits(
    TimeSpan SilenceGap,
    TimeSpan MinDuration,
    TimeSpan MaxDuration);
