using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// Synthesised speech is 24 kHz mono and a Discord voice connection takes 48 kHz stereo. The
/// conversion is arithmetic nothing watches at runtime, and it fails by producing audio that is
/// still audible — at the wrong pitch, or half the length.
/// </summary>
public class PcmUpsamplerTests
{
    private static byte[] Mono(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    private static short[] Samples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2);
        return samples;
    }

    [Fact]
    public void OneMonoSampleBecomesTwoStereoFrames()
    {
        byte[] stereo = PcmUpsampler.ToStereo48k(Mono(1000));

        Samples(stereo).Should().Equal(1000, 1000, 1000, 1000);
    }

    [Fact]
    public void OrderIsPreserved()
    {
        byte[] stereo = PcmUpsampler.ToStereo48k(Mono(100, -200));

        Samples(stereo).Should().Equal(100, 100, 100, 100, -200, -200, -200, -200);
    }

    [Fact]
    public void ASecondOfSpeechStaysASecond()
    {
        // Getting the ratio backwards produces audio at double or half speed, which is unmistakable
        // and would be embarrassing in a voice channel.
        byte[] second = new byte[24000 * 2];

        byte[] stereo = PcmUpsampler.ToStereo48k(second);

        stereo.Length.Should().Be(48000 * PcmUpsampler.TargetFrameBytes);
        PcmUpsampler.DurationOfStereo48k(stereo.Length).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void FullScaleAudioIsNotWrapped()
    {
        Samples(PcmUpsampler.ToStereo48k(Mono(short.MinValue, short.MaxValue)))
            .Should().OnlyContain(s => s == short.MinValue || s == short.MaxValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AudioTooShortToMakeASampleProducesNothing(int byteCount)
    {
        PcmUpsampler.ToStereo48k(new byte[byteCount]).Should().BeEmpty();
    }

    [Fact]
    public void TheTripFromDiscordAndBackKeepsItsLength()
    {
        // Not a round trip through the same rates, but the two conversions this bot does are the two
        // ends of the same call and a mistake in either shows up as a length that changed.
        byte[] fromDiscord = PcmDownsampler.ToMono16k(new byte[48000 * PcmDownsampler.SourceFrameBytes]);
        byte[] toDiscord = PcmUpsampler.ToStereo48k(new byte[24000 * 2]);

        PcmDownsampler.DurationOfMono16k(fromDiscord.Length).Should().Be(TimeSpan.FromSeconds(1));
        PcmUpsampler.DurationOfStereo48k(toDiscord.Length).Should().Be(TimeSpan.FromSeconds(1));
    }
}
