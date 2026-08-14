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
    public void IsShortEnoughToBeAMarkerRatherThanASound(VoiceChime chime)
    {
        double seconds = (double)Samples(chime).Length / VoiceChimes.SampleRate;

        seconds.Should().BeInRange(0.15, 0.5);
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
    public void TheListeningToneRises()
    {
        short[] samples = Samples(VoiceChime.Listening);
        int third = samples.Length / 3;

        Crossings(samples, 0, third)
            .Should().BeLessThan(Crossings(samples, samples.Length - third, samples.Length));
    }

    [Fact]
    public void TheWorkingToneFalls()
    {
        short[] samples = Samples(VoiceChime.Working);
        int third = samples.Length / 3;

        Crossings(samples, 0, third)
            .Should().BeGreaterThan(Crossings(samples, samples.Length - third, samples.Length));
    }

    [Fact]
    public void TheTwoTonesAreTheSameNotesInOppositeOrder()
    {
        // What makes the pair learnable without being explained. Two unrelated sounds would each have
        // to be learnt on its own.
        short[] rising = Samples(VoiceChime.Listening);
        short[] falling = Samples(VoiceChime.Working);
        int third = rising.Length / 3;

        rising.Length.Should().Be(falling.Length);

        Crossings(rising, 0, third)
            .Should().BeCloseTo(Crossings(falling, falling.Length - third, falling.Length), 12);
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
