using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// A reply written for a chat window read out loud is where markup stops being invisible: a
/// synthesiser voices a backtick, and a fenced stack trace is a minute of noise nobody can skip.
/// </summary>
public class SpokenTextTests
{
    [Fact]
    public void AShortDirectAnswerIsSpokenAsItIs()
    {
        // The answer this surface is for. Nothing to strip, nothing to cut.
        SpokenText.From("Yes, it is.").Should().Be("Yes, it is.");
    }

    [Fact]
    public void EmphasisAndCodeMarkersAreNotVoiced()
    {
        SpokenText.From("**minecraft** is running on `port 25565`")
            .Should().Be("minecraft is running on port 25565");
    }

    [Fact]
    public void AFencedBlockIsDroppedWhole()
    {
        // Flattening it would read a stack trace aloud. It is in the channel, which is where somebody
        // would go to read it.
        string reply = "It crashed. Here's the tail:\n```\nSegfault at 0x0\nat main()\n```\nI'd restart it.";

        SpokenText.From(reply).Should().Be("It crashed. Here's the tail: I'd restart it.");
    }

    [Fact]
    public void ListsAndHeadingsBecomeSentences()
    {
        string reply = "## Servers\n- minecraft is up\n- factorio is down";

        SpokenText.From(reply).Should().Be("Servers minecraft is up factorio is down");
    }

    [Fact]
    public void PunctuationThatShapesSpeechIsKept()
    {
        // Commas and full stops are what a synthesiser paces on; stripping them with the markup
        // would produce one flat run-on.
        SpokenText.From("Yes, it's running. It has been up for three hours!")
            .Should().Be("Yes, it's running. It has been up for three hours!");
    }

    [Fact]
    public void ALongAnswerIsSpokenInFull()
    {
        // ⚠ Nothing here decides that somebody has heard enough. A cap used to cut a reply at a
        // sentence boundary, which left the answer in the room disagreeing with the answer in the
        // channel and nothing anywhere to say which was which. How long a reply runs is the
        // assistant's to control — a spoken turn already asks for one written to be heard.
        string reply = string.Join(" ", Enumerable.Repeat("Minecraft is running.", 60));

        SpokenText.From(reply).Should().Be(reply);
    }

    [Fact]
    public void WhatIsSpokenIsAlwaysWhatWasWritten()
    {
        // The property that matters: this strips markup and must never reword anything. A surface
        // that paraphrases on the way to being read out says things the assistant did not.
        string reply = "Minecraft is running. Factorio is stopped. Ketchup is running.";

        SpokenText.From(reply).Should().Be(reply);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("```\njust a code block\n```")]
    public void NothingWorthSayingComesBackEmpty(string? reply)
    {
        SpokenText.From(reply).Should().BeEmpty();
    }
}

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
