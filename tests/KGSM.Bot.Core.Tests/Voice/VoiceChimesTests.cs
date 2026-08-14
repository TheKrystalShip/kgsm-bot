using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// The two tones, as a waveform.
/// </summary>
/// <remarks>
/// Everything asserted here is audible when it is wrong: a buffer in the wrong format is noise, one
/// that starts or ends on a non-zero sample clicks, one that clips tears, and two tones that move the
/// same way carry no meaning at all. None of it is catchable by reading the arithmetic.
/// </remarks>
public class VoiceChimesTests
{
    private const int BytesPerFrame = 4; // stereo, 16-bit

    /// <summary>Reads the buffer back as one mono track — both channels carry the same samples.</summary>
    private static short[] Samples(VoiceChime chime)
    {
        byte[] pcm = VoiceChimes.Pcm(chime);
        var samples = new short[pcm.Length / BytesPerFrame];

        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * BytesPerFrame);

        return samples;
    }

    /// <summary>
    /// How often the waveform crosses zero over a stretch — a pitch measurement that needs no
    /// transform, and enough to tell two notes a fourth apart from each other.
    /// </summary>
    private static int Crossings(short[] samples, int from, int to)
    {
        var crossings = 0;

        for (int i = from + 1; i < to; i++)
            if ((samples[i - 1] < 0) != (samples[i] < 0))
                crossings++;

        return crossings;
    }

    [Theory]
    [InlineData(VoiceChime.Listening)]
    [InlineData(VoiceChime.Working)]
    public void IsWrittenInTheFormatAVoiceConnectionReads(VoiceChime chime)
    {
        // 48 kHz stereo signed 16-bit. A buffer whose length is not a whole number of frames is
        // written to the stream one byte out of alignment and every sample after it is garbage.
        byte[] pcm = VoiceChimes.Pcm(chime);

        pcm.Length.Should().BeGreaterThan(0);
        (pcm.Length % BytesPerFrame).Should().Be(0);
    }

    [Theory]
    [InlineData(VoiceChime.Listening)]
    [InlineData(VoiceChime.Working)]
    public void RingsForExactlyWhatTheStreamWillSend(VoiceChime chime)
    {
        // Anything shorter is padded out to this length before it goes anywhere, so a shorter tone
        // would occupy the connection for the same time and spend the difference on silence.
        VoiceChimes.Pcm(chime).Length.Should().Be(SendableAudio.PreloadBytes);
    }

    [Theory]
    [InlineData(VoiceChime.Listening)]
    [InlineData(VoiceChime.Working)]
    public void TheNoteMellowsAsItRings(VoiceChime chime)
    {
        // The one property that separates a struck note from a beep: the upper partials die away
        // faster than the fundamental, so the sound is brighter at the start than in the tail. Equal
        // envelopes give a shape that never changes, which is heard as electronic.
        // Measured inside the FIRST note only, before the second is struck — across the whole tone
        // the pitch changes on purpose, and that movement would swamp what this is looking at.
        short[] samples = Samples(chime);

        double early = Brightness(samples, At(0.03), At(0.08));
        double late = Brightness(samples, At(0.13), At(0.18));

        early.Should().BeGreaterThan(late, "the partials on top should fade first");
    }

    private static int At(double seconds) => (int)(seconds * VoiceChimes.SampleRate);

    /// <summary>
    /// How much of a stretch's energy is in its upper partials.
    /// </summary>
    /// <remarks>
    /// The energy of the sample-to-sample difference against the energy of the signal — a crude
    /// high-pass, which is all that is needed to tell a bright stretch from a mellow one. Counting
    /// zero crossings does not work here: the fundamental dominates them, so a note whose overtones
    /// have completely died away still crosses zero exactly as often.
    /// </remarks>
    private static double Brightness(short[] samples, int from, int to)
    {
        double energy = 0;
        double difference = 0;

        for (int i = from + 1; i < to; i++)
        {
            double x = samples[i];
            double d = samples[i] - samples[i - 1];
            energy += x * x;
            difference += d * d;
        }

        return energy <= 0 ? 0 : difference / energy;
    }

    [Theory]
    [InlineData(VoiceChime.Listening)]
    [InlineData(VoiceChime.Working)]
    public void StartsAndEndsOnSilence(VoiceChime chime)
    {
        // A buffer that begins or ends part-way up the waveform is a step change, which is heard as a
        // click on the front or back of every single tone.
        short[] samples = Samples(chime);

        samples[0].Should().Be(0);
        samples[^1].Should().Be(0);
    }

    [Theory]
    [InlineData(VoiceChime.Listening)]
    [InlineData(VoiceChime.Working)]
    public void NeverClipsAndStaysUnderSpeech(VoiceChime chime)
    {
        // The two notes overlap, so the mix can be louder than either. Clipping tears; merely being
        // loud trains people to turn down the volume the answers come out of.
        short peak = Samples(chime).Max(Math.Abs) is var loudest and <= short.MaxValue
            ? (short)loudest
            : short.MaxValue;

        peak.Should().BeLessThan((short)(short.MaxValue * 0.5),
            "a marker must not arrive louder than the answer it introduces");
        peak.Should().BeGreaterThan((short)(short.MaxValue * 0.05),
            "a tone nobody can hear reports nothing");
    }

    [Fact]
    public void TheTonesMoveInOppositeDirections()
    {
        // ⚠ The pair is compared against ITSELF at the same instant rather than each tone against its
        // own tail. Crossings answer to brightness as well as pitch, and both tones start bright and
        // mellow as they ring — so an early-against-late reading inside one tone measures the decay
        // as much as the bend. At the same moment the two share a timbre, and only the pitch differs,
        // which is exactly the thing being asserted.
        short[] rising = Samples(VoiceChime.Listening);
        short[] falling = Samples(VoiceChime.Working);

        // Just after the strike, where the bend has barely started.
        Crossings(rising, At(0.02), At(0.08))
            .Should().BeLessThan(Crossings(falling, At(0.02), At(0.08)),
                "the rising tone starts from the lower pitch");

        // After the bend has settled, and while there is still something to measure.
        Crossings(rising, At(0.35), At(0.55))
            .Should().BeGreaterThan(Crossings(falling, At(0.35), At(0.55)),
                "and settles on the higher one");
    }

    [Fact]
    public void TheTwoTonesAreMadeOfTheSameMaterial()
    {
        // What makes the pair learnable without being explained: one sound played two ways, rather
        // than two sounds each of which has to be learnt. The same notes with the same envelopes in
        // the opposite order carry the same energy, so a drift here means one of them has quietly
        // become a different sound.
        short[] rising = Samples(VoiceChime.Listening);
        short[] falling = Samples(VoiceChime.Working);

        rising.Length.Should().Be(falling.Length);

        Rms(rising).Should().BeApproximately(Rms(falling), Rms(rising) * 0.15);
    }

    private static double Rms(short[] samples)
    {
        double sum = 0;
        foreach (short sample in samples) sum += (double)sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    [Fact]
    public void IsRenderedOnceAndHandedOutByReference()
    {
        // The whole point of a tone over a spoken phrase is that it costs nothing at the moment it is
        // needed. Re-rendering per play would put arithmetic back on the one path that exists to be
        // immediate.
        VoiceChimes.Pcm(VoiceChime.Listening)
            .Should().BeSameAs(VoiceChimes.Pcm(VoiceChime.Listening));
    }

    [Fact]
    public void TheTwoTonesAreDifferentBuffers()
    {
        VoiceChimes.Pcm(VoiceChime.Listening)
            .Should().NotBeSameAs(VoiceChimes.Pcm(VoiceChime.Working));
    }
}
