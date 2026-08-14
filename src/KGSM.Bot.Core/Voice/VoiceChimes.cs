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
/// Two struck notes, rendered once into the format a voice channel is written in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tone says what a spoken acknowledgement says, in a fraction of the time and without wearing
/// out.</b> "Looking into it" is information the first few times and noise by the twentieth, which is
/// the same fatigue that makes a wake phrase tiresome to repeat. A tone is not a sentence competing
/// for attention with the answer that follows it — and it costs no synthesis, so the first sample
/// arrives immediately rather than after a model has produced a waveform.
/// </para>
/// <para>
/// <b>A full second of ring is free.</b> The output stream will not transmit less than
/// <see cref="SendableAudio.BufferMillis"/> whatever it is given (see there), so a tenth of a second
/// of tone and a whole second of it occupy the connection for exactly as long. The only question is
/// whether the rest is silence or decay, and decay is what makes a struck note sound struck.
/// </para>
/// <para>
/// <b>What separates an instrument from a beep is the envelope, not the waveform.</b> Three things do
/// the work here, and dropping any one of them is audible: the upper partials <em>decay faster than
/// the fundamental</em>, which is what a struck object does and a synthesiser does not; the partials
/// sit slightly off the exact harmonic ratios, because perfectly integer overtones are a sound nothing
/// physical makes; and the attack is a curve rather than a ramp, since a straight line into full
/// amplitude is heard as a click on the front of the note.
/// </para>
/// <para>
/// <b>Reflections, because a completely dry sound is the other half of "digital".</b> A few quiet
/// delayed copies is not reverb in any real sense — it is just enough early reflection that the tone
/// sounds like it happened somewhere rather than being injected straight into the stream.
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
    /// The two ends of one note's inflection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Two struck notes a fourth apart is a doorbell</b>, however the timbre is tuned — the
    /// melody is the thing being recognised, and everybody already knows that one. So there is one
    /// note, and the direction is carried by bending its pitch instead.
    /// </para>
    /// <para>
    /// <b>Which is also the more natural way to say it.</b> A voice rises at the end of a question and
    /// falls to finish a statement, and that is exactly what these two mean — "your turn?" and "got
    /// it." A whole tone is enough to hear as inflection and small enough not to become a tune.
    /// </para>
    /// </remarks>
    private const double Settled = 440.00; // A4
    private const double Raised = 493.88;  // B4

    /// <summary>How long the note is allowed to ring.</summary>
    private const double NoteSeconds = 0.70;

    /// <summary>
    /// How long the pitch takes to arrive where it is going.
    /// </summary>
    /// <remarks>
    /// Slow enough to be heard as a bend rather than as two pitches, and finished well before the
    /// note has faded, so the direction is carried by the loud part of the sound.
    /// </remarks>
    private const double GlideSeconds = 0.28;

    /// <summary>
    /// How long the note takes to reach full amplitude, as a raised cosine rather than a ramp.
    /// </summary>
    /// <remarks>
    /// Short enough to still be a struck note; long enough that the leading edge is a swell rather
    /// than a step. A linear attack of a few milliseconds is what makes a tone sound like a test
    /// signal.
    /// </remarks>
    private const double AttackSeconds = 0.028;

    /// <summary>
    /// The partials that make up one note: how far above the fundamental, how loud, and how fast each
    /// dies away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The decay column is the important one.</b> Higher partials fading faster than the
    /// fundamental is what a physical object does when struck — the note starts bright and mellows as
    /// it rings. Giving every partial the same envelope produces a sound that never changes shape,
    /// which is heard as electronic however many partials are stacked up.
    /// </para>
    /// <para>
    /// <b>The ratios are deliberately not whole numbers.</b> A struck bar or bell has stretched
    /// partials; exact multiples are a property of arithmetic rather than of anything that rings. The
    /// near-unison at the top of the list is the same idea applied twice over — two voices a fifth of
    /// a hertz apart beat slowly against each other, which reads as warmth.
    /// </para>
    /// </remarks>
    private static readonly (double Ratio, double Amplitude, double Decay)[] Partials =
    [
        (0.500, 0.20, 3.2),   // the body under it, the way a struck box answers the bar
        (1.000, 1.00, 4.5),
        (2.001, 0.20, 9.0),
        (3.902, 0.30, 16.0),  // what makes wood sound like wood rather than metal
        (6.810, 0.07, 30.0),  // the mallet itself, gone almost before it is heard
    ];

    /// <summary>
    /// Quiet delayed copies, standing in for the first reflections off a room.
    /// </summary>
    /// <remarks>
    /// Prime-ish spacings so the repeats do not line up into an audible flutter, and quiet enough to
    /// be felt rather than heard as echoes.
    /// </remarks>
    private static readonly (double Seconds, double Gain)[] Reflections =
    [
        (0.019, 0.16),
        (0.037, 0.11),
        (0.061, 0.07),
    ];

    /// <summary>Peak amplitude, well under full scale so a tone never arrives louder than the answer.</summary>
    private const double Peak = 0.22;

    /// <summary>
    /// The final ramp to true silence.
    /// </summary>
    /// <remarks>
    /// The decay never actually reaches zero, and a buffer that stops on a non-zero sample is a step
    /// change in the waveform — heard as a click on the end of every single tone.
    /// </remarks>
    private const double ReleaseSeconds = 0.030;

    private static readonly byte[] RisingPcm = Render(Settled, Raised);
    private static readonly byte[] FallingPcm = Render(Raised, Settled);

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

    /// <summary>One struck note bending from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static byte[] Render(double from, double to)
    {
        // Sized to what the output stream will send rather than to the note, since anything shorter
        // is padded out to exactly this length anyway. The ring gets the room for free.
        int frames = SendableAudio.PreloadBytes / (Channels * BytesPerSample);

        var mix = new double[frames];
        Strike(mix, from, to);

        Reflect(mix);
        Level(mix);
        Release(mix);

        var pcm = new byte[frames * Channels * BytesPerSample];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (short)(mix[frame] * short.MaxValue);

            int at = frame * Channels * BytesPerSample;
            for (var channel = 0; channel < Channels; channel++)
            {
                pcm[at + (channel * BytesPerSample)] = (byte)(sample & 0xFF);
                pcm[at + (channel * BytesPerSample) + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        return pcm;
    }

    /// <summary>Rings the note, bending it as it goes.</summary>
    /// <remarks>
    /// ⚠ <b>Phase is accumulated, never computed from the elapsed time.</b> With a frequency that
    /// changes, <c>sin(2πf(t)·t)</c> is not a note that bends — it is a note whose phase jumps on
    /// every sample, which is heard as a rasp rather than as a pitch.
    /// </remarks>
    private static void Strike(double[] mix, double from, double to)
    {
        var length = (int)(NoteSeconds * SampleRate);
        var phase = new double[Partials.Length];

        for (var i = 0; i < length && i < mix.Length; i++)
        {
            double t = (double)i / SampleRate;
            double frequency = Glide(from, to, t);
            double value = 0;

            for (var p = 0; p < Partials.Length; p++)
            {
                (double ratio, double amplitude, double decay) = Partials[p];

                value += amplitude * Math.Exp(-decay * t) * Math.Sin(phase[p]);
                phase[p] += 2.0 * Math.PI * frequency * ratio / SampleRate;
            }

            mix[i] += Attack(t) * value;
        }
    }

    /// <summary>Where the pitch has reached — eased at both ends, so it bends and settles.</summary>
    private static double Glide(double from, double to, double t)
    {
        if (t >= GlideSeconds) return to;

        double along = 0.5 - (0.5 * Math.Cos(Math.PI * t / GlideSeconds));
        return from + ((to - from) * along);
    }

    /// <summary>A raised cosine into full amplitude — a swell, not a step.</summary>
    private static double Attack(double t) =>
        t >= AttackSeconds ? 1.0 : 0.5 - (0.5 * Math.Cos(Math.PI * t / AttackSeconds));

    /// <summary>Folds a few quiet delayed copies back in, so the tone sounds like it happened somewhere.</summary>
    private static void Reflect(double[] mix)
    {
        var dry = (double[])mix.Clone();

        foreach ((double seconds, double gain) in Reflections)
        {
            var delay = (int)(seconds * SampleRate);

            for (int at = delay; at < mix.Length; at++)
                mix[at] += dry[at - delay] * gain;
        }
    }

    /// <summary>
    /// Scales the whole thing to <see cref="Peak"/>.
    /// </summary>
    /// <remarks>
    /// Measured rather than budgeted. Five partials, two overlapping notes and four reflections sum to
    /// a level nobody can predict by reading the constants — and getting it wrong means either
    /// clipping, which tears, or a tone too quiet to do its job. Scaling what was actually rendered
    /// also means the partials can be retuned without anybody having to re-derive the headroom.
    /// </remarks>
    private static void Level(double[] mix)
    {
        double loudest = 0;
        foreach (double sample in mix) loudest = Math.Max(loudest, Math.Abs(sample));

        if (loudest <= 0) return;

        double scale = Peak / loudest;
        for (var i = 0; i < mix.Length; i++) mix[i] *= scale;
    }

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
