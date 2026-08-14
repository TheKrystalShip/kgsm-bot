using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// What Discord delivers is 48 kHz stereo and what a recogniser wants is 16 kHz mono, and the
/// conversion between them is arithmetic nobody watches at runtime — a downsampler that quietly
/// halves the volume, drops a channel, or shifts the pitch produces audio that still sounds like
/// speech and transcribes badly.
/// </summary>
public class PcmDownsamplerTests
{
    /// <summary>Builds 48 kHz stereo PCM from per-channel sample values.</summary>
    private static byte[] Stereo(params (short Left, short Right)[] frames)
    {
        var bytes = new byte[frames.Length * 4];
        for (int i = 0; i < frames.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), frames[i].Left);
            BitConverter.TryWriteBytes(bytes.AsSpan((i * 4) + 2), frames[i].Right);
        }
        return bytes;
    }

    private static short[] Samples(byte[] mono)
    {
        var samples = new short[mono.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(mono, i * 2);
        return samples;
    }

    [Fact]
    public void ThreeStereoFramesBecomeOneMonoSample()
    {
        byte[] mono = PcmDownsampler.ToMono16k(Stereo((100, 100), (100, 100), (100, 100)));

        mono.Length.Should().Be(2, "three 48 kHz frames are one 16 kHz sample");
        Samples(mono)[0].Should().Be(100, "a constant signal must come through at its own level");
    }

    [Fact]
    public void BothChannelsAreHeard()
    {
        // A speaker panned hard to one side is somebody whose other channel is silence. Taking one
        // channel would drop them entirely; averaging keeps them at half level, which is audible.
        byte[] mono = PcmDownsampler.ToMono16k(Stereo((1200, 0), (1200, 0), (1200, 0)));

        Samples(mono)[0].Should().Be(600);
    }

    [Fact]
    public void TheThreeSamplesAreAveragedRatherThanPicked()
    {
        // Picking one of the three and discarding the rest is the aliasing bug: it folds everything
        // above 8 kHz back over the speech band. The average of a spike and two zeroes is the
        // evidence that all three were looked at.
        byte[] mono = PcmDownsampler.ToMono16k(Stereo((3000, 3000), (0, 0), (0, 0)));

        Samples(mono)[0].Should().Be(1000);
    }

    [Fact]
    public void AudioLongerThanOneGroupKeepsItsOrder()
    {
        byte[] mono = PcmDownsampler.ToMono16k(Stereo(
            (300, 300), (300, 300), (300, 300),
            (-900, -900), (-900, -900), (-900, -900)));

        Samples(mono).Should().Equal(300, -900);
    }

    [Fact]
    public void ATrailingPartialGroupIsDroppedRatherThanPadded()
    {
        // Two leftover frames are 42 microseconds. Padding them to a full group with silence would
        // write a discontinuity into audio that was never missing.
        byte[] mono = PcmDownsampler.ToMono16k(Stereo(
            (500, 500), (500, 500), (500, 500), (500, 500), (500, 500)));

        Samples(mono).Should().Equal(500);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    public void AudioTooShortToMakeASampleProducesNothing(int byteCount)
    {
        PcmDownsampler.ToMono16k(new byte[byteCount]).Should().BeEmpty();
    }

    [Fact]
    public void NegativeSamplesSurviveTheAverage()
    {
        // Signed arithmetic done with the wrong type turns a quiet negative sample into a very loud
        // positive one, which is a click rather than a subtle error.
        byte[] mono = PcmDownsampler.ToMono16k(Stereo((-2000, -2000), (-2000, -2000), (-2000, -2000)));

        Samples(mono)[0].Should().Be(-2000);
    }

    [Fact]
    public void FullScaleAudioIsNotWrapped()
    {
        byte[] mono = PcmDownsampler.ToMono16k(Stereo(
            (short.MinValue, short.MinValue), (short.MinValue, short.MinValue), (short.MinValue, short.MinValue)));

        Samples(mono)[0].Should().Be(short.MinValue);
    }

    [Fact]
    public void ASecondOfAudioIsReportedAsASecond()
    {
        // 16 kHz, two bytes a sample. Duration is read off the byte count everywhere else, so this
        // being wrong would mis-state every utterance's length and mis-trigger the ceiling.
        PcmDownsampler.DurationOfMono16k(32000).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void OneSecondOfDiscordAudioConvertsToOneSecondOfRecogniserAudio()
    {
        // The end-to-end shape: 48 kHz stereo in, 16 kHz mono out, same wall-clock length.
        byte[] second = new byte[48000 * PcmDownsampler.SourceFrameBytes];

        byte[] mono = PcmDownsampler.ToMono16k(second);

        mono.Length.Should().Be(16000 * 2);
        PcmDownsampler.DurationOfMono16k(mono.Length).Should().Be(TimeSpan.FromSeconds(1));
    }
}
