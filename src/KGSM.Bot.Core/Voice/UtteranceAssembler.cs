namespace KGSM.Bot.Core.Voice;

/// <summary>
/// Collects one speaker's audio into utterances — the unit a recogniser is given and a person
/// recognises as "something I said".
/// </summary>
/// <remarks>
/// <para>
/// Discord sends frames only while somebody is actually talking, so the gaps are already in the
/// stream and no voice-activity detection is needed to find them: the absence of a frame <em>is</em>
/// the silence. What is left is deciding how long a gap has to be before it ends a sentence rather
/// than sitting inside one, which is what <see cref="UtteranceLimits.SilenceGap"/> is.
/// </para>
/// <para>
/// The clock is passed in rather than read. A boundary rule expressed in durations is worth testing
/// at its edges, and a class that reads <c>DateTimeOffset.UtcNow</c> can only be tested by waiting.
/// </para>
/// <para>
/// One of these per speaker. Two people talking at once are two streams and two assemblers, which is
/// why nothing here has to separate voices.
/// </para>
/// </remarks>
public sealed class UtteranceAssembler(ulong speakerId, string speakerName, UtteranceLimits limits)
{
    private readonly List<byte> _audio = [];
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastAudioAt;

    /// <summary>Whether audio is being collected right now.</summary>
    public bool IsCollecting => _audio.Count > 0;

    /// <summary>How much audio is currently held.</summary>
    public TimeSpan Buffered => PcmDownsampler.DurationOfMono16k(_audio.Count);

    /// <summary>
    /// Adds 16 kHz mono PCM. Returns an utterance when this audio takes the buffer past
    /// <see cref="UtteranceLimits.MaxDuration"/>, and null otherwise.
    /// </summary>
    public VoiceUtterance? Append(ReadOnlySpan<byte> mono16k, DateTimeOffset now)
    {
        if (mono16k.IsEmpty) return null;

        if (!IsCollecting) _startedAt = now;
        _lastAudioAt = now;
        _audio.AddRange(mono16k);

        // Cut at the ceiling rather than after crossing it, so the cap is a bound on what is held
        // rather than a bound on what is held plus one frame.
        return Buffered >= limits.MaxDuration ? Take() : null;
    }

    /// <summary>
    /// Ends the current utterance if the speaker has been quiet for
    /// <see cref="UtteranceLimits.SilenceGap"/>, or immediately when <paramref name="force"/> is set —
    /// which is what a speaker leaving the channel means, since no further audio is coming.
    /// </summary>
    /// <remarks>
    /// Returns null both when there is nothing to end and when what was collected is too short to be
    /// worth passing on. Those are the same to a caller: no utterance happened.
    /// </remarks>
    public VoiceUtterance? Close(DateTimeOffset now, bool force = false)
    {
        if (!IsCollecting) return null;
        if (!force && now - _lastAudioAt < limits.SilenceGap) return null;

        VoiceUtterance utterance = Take();
        return utterance.Duration >= limits.MinDuration ? utterance : null;
    }

    private VoiceUtterance Take()
    {
        var audio = _audio.ToArray();
        _audio.Clear();
        return new VoiceUtterance(
            speakerId, speakerName, audio, PcmDownsampler.DurationOfMono16k(audio.Length), _startedAt);
    }
}
