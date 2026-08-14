namespace KGSM.Bot.Core.Voice;

/// <summary>
/// The two tones the bot marks its listening state with.
/// </summary>
/// <remarks>
/// <b>Direction carries the whole meaning.</b> They are the same pair of notes played in opposite
/// order, which is what makes them learnable without being explained — every device a person already
/// owns rises to open something and falls to close it.
/// </remarks>
public enum VoiceChime
{
    /// <summary>Rising: the floor is yours, and a window is counting down.</summary>
    Listening,

    /// <summary>Falling: the request has been taken and is being worked on.</summary>
    Working,
}

/// <summary>
/// Two short tones, rendered once into the format a voice channel is written in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tone says what a spoken acknowledgement says, in a fifth of the time and without wearing
/// out.</b> "Looking into it" is information the first few times and noise by the twentieth, which is
/// the same fatigue that makes a wake phrase tiresome to repeat. A tone is not a sentence competing
/// for attention with the answer that follows it — and it costs no synthesis, so the first sample
/// arrives immediately rather than after a model has produced a waveform.
/// </para>
/// <para>
/// <b>Synthesised rather than shipped.</b> A generated tone is a few lines of arithmetic with no
/// binary asset to license, store or lose, and its pitch and length stay adjustable by editing the
/// numbers below. What it must not be is naive: a bare sine reads as a cheap beep, so each note is a
/// fundamental with two quiet partials over it and a struck-bell envelope, which is what makes it
/// sound like an instrument rather than a test signal.
/// </para>
/// <para>
/// <b>It is quieter than speech on purpose.</b> A marker that is louder than the answer it introduces
/// trains people to turn the bot down, and the volume they turn down is the same one the answer comes
/// out of.
/// </para>
/// </remarks>
public static class VoiceChimes
{
    /// <summary>The format a Discord voice connection is written in: 48 kHz stereo signed 16-bit.</summary>
    public const int SampleRate = 48_000;

    private const int Channels = 2;
    private const int BytesPerSample = 2;

    /// <summary>
    /// The interval, as two frequencies. A perfect fourth — wide enough to read as movement, narrow
    /// enough not to sound like an alarm.
    /// </summary>
    private const double Low = 783.99;   // G5
    private const double High = 1046.50; // C6

    /// <summary>How long one note rings for.</summary>
    private const double NoteSeconds = 0.18;

    /// <summary>
    /// When the second note starts. Less than a note's length, so the two overlap and the pair is
    /// heard as one gesture rather than as two separate beeps.
    /// </summary>
    private const double SecondNoteAt = 0.11;

    /// <summary>Long enough to avoid a click at the attack, short enough to still sound struck.</summary>
    private const double AttackSeconds = 0.006;

    /// <summary>How fast a note dies away, per second. Tuned so it is nearly gone by its own end.</summary>
    private const double DecaySeconds = 13.0;

    /// <summary>
    /// The final ramp to true silence.
    /// </summary>
    /// <remarks>
    /// The exponential never actually reaches zero, and a buffer that stops on a non-zero sample is a
    /// step change in the waveform — which is heard as a click on the end of every single tone.
    /// </remarks>
    private const double ReleaseSeconds = 0.015;

    /// <summary>Peak amplitude, well under full scale so a tone never arrives louder than the answer.</summary>
    private const double Peak = 0.22;

    private static readonly byte[] RisingPcm = Render(Low, High);
    private static readonly byte[] FallingPcm = Render(High, Low);

    /// <summary>
    /// The tone, ready to write to a voice connection.
    /// </summary>
    /// <remarks>
    /// Rendered once at startup and handed out by reference: it is read-only in practice, and a tone
    /// regenerated per play would burn arithmetic on the one path whose whole purpose is to be
    /// immediate.
    /// </remarks>
    public static byte[] Pcm(VoiceChime chime) => chime switch
    {
        VoiceChime.Listening => RisingPcm,
        VoiceChime.Working => FallingPcm,
        _ => FallingPcm,
    };

    /// <summary>Two overlapping notes, mixed and written out as interleaved stereo.</summary>
    private static byte[] Render(double first, double second)
    {
        double totalSeconds = SecondNoteAt + NoteSeconds;
        var frames = (int)(totalSeconds * SampleRate);

        var mix = new double[frames];
        Add(mix, first, 0);
        Add(mix, second, (int)(SecondNoteAt * SampleRate));

        Release(mix);

        var pcm = new byte[frames * Channels * BytesPerSample];
        for (var frame = 0; frame < frames; frame++)
        {
            // Clamped rather than trusted: the two notes overlap, and a mix that ran past full scale
            // would wrap to the opposite sign, which is heard as a tear rather than as loudness.
            double value = Math.Clamp(mix[frame], -1.0, 1.0);
            var sample = (short)(value * short.MaxValue);

            int at = frame * Channels * BytesPerSample;
            for (var channel = 0; channel < Channels; channel++)
            {
                pcm[at + (channel * BytesPerSample)] = (byte)(sample & 0xFF);
                pcm[at + (channel * BytesPerSample) + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        return pcm;
    }

    /// <summary>Rings one note into the mix, starting at <paramref name="offset"/>.</summary>
    private static void Add(double[] mix, double frequency, int offset)
    {
        var length = (int)(NoteSeconds * SampleRate);

        for (var i = 0; i < length; i++)
        {
            int at = offset + i;
            if (at >= mix.Length) break;

            double t = (double)i / SampleRate;
            double angle = 2.0 * Math.PI * frequency * t;

            // The partials are what stop it sounding like a test tone. Quiet enough that the note's
            // pitch is unambiguous, present enough to give it a body.
            double tone = Math.Sin(angle)
                          + (0.25 * Math.Sin(2.0 * angle))
                          + (0.08 * Math.Sin(3.0 * angle));

            mix[at] += Envelope(t) * tone * Peak;
        }
    }

    /// <summary>A struck note: up fast, then dying away.</summary>
    private static double Envelope(double t) =>
        t < AttackSeconds
            ? t / AttackSeconds
            : Math.Exp(-DecaySeconds * (t - AttackSeconds));

    /// <summary>Ramps the tail to exactly zero, so the buffer ends on silence.</summary>
    private static void Release(double[] mix)
    {
        var length = (int)(ReleaseSeconds * SampleRate);
        if (length <= 0 || length > mix.Length) return;

        int from = mix.Length - length;
        for (var i = 0; i < length; i++)
            mix[from + i] *= 1.0 - ((double)i / length);
    }
}
