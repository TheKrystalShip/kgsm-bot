namespace KGSM.Bot.Core.Voice;

/// <summary>
/// Turns the PCM Discord delivers into the PCM speech recognition expects: 48 kHz stereo,
/// 16-bit signed little-endian, down to 16 kHz mono.
/// </summary>
/// <remarks>
/// <para>
/// The ratio is exactly 3:1, which is the only reason this is arithmetic rather than a resampling
/// library — 48000 and 16000 have no fractional relationship to carry, so each output sample is a
/// whole number of input samples and nothing accumulates across a call.
/// </para>
/// <para>
/// Averaging the three, rather than taking one of them and discarding two, is what keeps the
/// decimation honest. Dropping samples folds everything above 8 kHz back down over the speech band
/// as alias — audible as a metallic edge, and worse than audible to a recogniser, because the noise
/// it adds sits exactly where consonants are distinguished. A three-tap mean is a crude low-pass and
/// not a good one, but it attenuates rather than folds, and speech recognition at 16 kHz is not
/// sensitive to the difference between this and a designed filter.
/// </para>
/// <para>
/// Stereo is averaged rather than one channel taken: a speaker panned hard to one side is somebody
/// whose channel would otherwise be silence.
/// </para>
/// </remarks>
public static class PcmDownsampler
{
    /// <summary>Samples of 48 kHz input that make one sample of 16 kHz output.</summary>
    private const int Ratio = 3;

    /// <summary>Bytes in one 48 kHz stereo frame: two channels, two bytes each.</summary>
    public const int SourceFrameBytes = 4;

    /// <summary>
    /// Converts <paramref name="source"/> — 48 kHz stereo signed 16-bit LE — to 16 kHz mono of the
    /// same sample format.
    /// </summary>
    /// <remarks>
    /// A trailing partial group is dropped rather than padded. It is at most two samples, 42
    /// microseconds, and padding it with silence would write a discontinuity into the output for
    /// audio that was never missing.
    /// </remarks>
    public static byte[] ToMono16k(ReadOnlySpan<byte> source)
    {
        int sourceFrames = source.Length / SourceFrameBytes;
        int outputSamples = sourceFrames / Ratio;
        if (outputSamples == 0) return [];

        var output = new byte[outputSamples * 2];

        for (int i = 0; i < outputSamples; i++)
        {
            // Sum three stereo frames as one running total of six channel samples, then divide once:
            // averaging the pair and then averaging the triple rounds twice, and the second rounding
            // operates on numbers the first one already moved.
            int sum = 0;
            int baseOffset = i * Ratio * SourceFrameBytes;

            for (int f = 0; f < Ratio; f++)
            {
                int offset = baseOffset + (f * SourceFrameBytes);
                sum += BitConverter.ToInt16(source[offset..]);
                sum += BitConverter.ToInt16(source[(offset + 2)..]);
            }

            int average = sum / (Ratio * 2);

            // The average of in-range samples cannot leave the range, so this clamp is not reachable
            // by arithmetic — it is here so that a future change to the window cannot silently wrap a
            // loud sample into the opposite sign, which is a click rather than a quiet error.
            short sample = (short)Math.Clamp(average, short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(i * 2), sample);
        }

        return output;
    }

    /// <summary>How long <paramref name="monoByteCount"/> bytes of 16 kHz mono audio lasts.</summary>
    public static TimeSpan DurationOfMono16k(int monoByteCount) =>
        TimeSpan.FromSeconds(monoByteCount / 2.0 / 16000.0);
}

/// <summary>
/// Turns synthesised speech into the PCM Discord will accept: 24 kHz mono, 16-bit signed
/// little-endian, up to 48 kHz stereo.
/// </summary>
/// <remarks>
/// <para>
/// The inverse trip to <see cref="PcmDownsampler"/>, and simpler for it. The ratio is exactly 2:1 in
/// the other direction, so each input sample becomes two output samples and nothing accumulates.
/// </para>
/// <para>
/// Each sample is repeated rather than interpolated between neighbours. Duplication mirrors the
/// spectrum above 12 kHz into the top of the output band, which is real distortion — and it sits
/// above where speech carries meaning, in a signal that is about to be Opus-encoded for a voice call
/// at a bitrate that discards most of that region anyway. Interpolating would be more correct and
/// inaudible here; sample-and-hold is what the Opus encoder is going to smooth regardless.
/// </para>
/// <para>
/// Mono becomes two identical channels because that is what Discord's stream expects, not because
/// there is anything stereo about one synthesised voice.
/// </para>
/// </remarks>
public static class PcmUpsampler
{
    /// <summary>Bytes in one 48 kHz stereo frame: two channels, two bytes each.</summary>
    public const int TargetFrameBytes = 4;

    /// <summary>
    /// Converts <paramref name="mono24k"/> — 24 kHz mono signed 16-bit LE — to the 48 kHz stereo of
    /// the same sample format that a Discord voice stream is written in.
    /// </summary>
    public static byte[] ToStereo48k(ReadOnlySpan<byte> mono24k)
    {
        int samples = mono24k.Length / 2;
        if (samples == 0) return [];

        // One input sample becomes two output samples, each of two channels.
        var output = new byte[samples * 2 * TargetFrameBytes];

        for (int i = 0; i < samples; i++)
        {
            short sample = BitConverter.ToInt16(mono24k[(i * 2)..]);
            int at = i * 2 * TargetFrameBytes;

            for (int repeat = 0; repeat < 2; repeat++)
            {
                int offset = at + (repeat * TargetFrameBytes);
                BitConverter.TryWriteBytes(output.AsSpan(offset), sample);
                BitConverter.TryWriteBytes(output.AsSpan(offset + 2), sample);
            }
        }

        return output;
    }

    /// <summary>How long <paramref name="stereoByteCount"/> bytes of 48 kHz stereo audio lasts.</summary>
    public static TimeSpan DurationOfStereo48k(int stereoByteCount) =>
        TimeSpan.FromSeconds(stereoByteCount / (double)TargetFrameBytes / 48000.0);
}
