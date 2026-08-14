using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// Where one utterance ends is the whole behaviour of the capture half: cut too eagerly and a
/// sentence reaches the recogniser in halves, cut too late and every answer waits. The rule is
/// expressed in durations, so it is worth testing at its edges — which is what passing the clock in
/// rather than reading it makes possible.
/// </summary>
public class UtteranceAssemblerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly UtteranceLimits Limits = new(
        SilenceGap: TimeSpan.FromMilliseconds(800),
        MinDuration: TimeSpan.FromMilliseconds(400),
        MaxDuration: TimeSpan.FromSeconds(20));

    private static UtteranceAssembler Assembler(UtteranceLimits? limits = null) =>
        new(speakerId: 42, speakerName: "heisen", limits ?? Limits);

    /// <summary>16 kHz mono is 32 bytes per millisecond.</summary>
    private static byte[] Audio(int milliseconds) => new byte[milliseconds * 32];

    [Fact]
    public void AppendingAudioDoesNotOnItsOwnEndAnUtterance()
    {
        UtteranceAssembler assembler = Assembler();

        assembler.Append(Audio(500), T0).Should().BeNull();
        assembler.IsCollecting.Should().BeTrue();
    }

    [Fact]
    public void SilenceLongerThanTheGapEndsTheUtterance()
    {
        UtteranceAssembler assembler = Assembler();
        assembler.Append(Audio(1000), T0);

        VoiceUtterance? utterance = assembler.Close(T0.AddMilliseconds(800));

        utterance.Should().NotBeNull();
        utterance!.Duration.Should().Be(TimeSpan.FromSeconds(1));
        utterance.SpeakerId.Should().Be(42);
        utterance.SpeakerName.Should().Be("heisen");
        utterance.StartedAt.Should().Be(T0);
        assembler.IsCollecting.Should().BeFalse();
    }

    [Fact]
    public void APauseShorterThanTheGapSitsInsideTheSentence()
    {
        // The failure this prevents is the common one: somebody drawing breath mid-sentence gets
        // their sentence delivered in two halves, and neither half means what they said.
        UtteranceAssembler assembler = Assembler();
        assembler.Append(Audio(600), T0);

        assembler.Close(T0.AddMilliseconds(799)).Should().BeNull();

        assembler.Append(Audio(600), T0.AddMilliseconds(799));
        VoiceUtterance? utterance = assembler.Close(T0.AddMilliseconds(1700));

        utterance!.Duration.Should().Be(TimeSpan.FromMilliseconds(1200), "both halves are one utterance");
    }

    [Fact]
    public void TheSilenceGapIsMeasuredFromTheLastAudioNotFromTheStart()
    {
        UtteranceAssembler assembler = Assembler();
        assembler.Append(Audio(300), T0);
        assembler.Append(Audio(300), T0.AddSeconds(5));

        // Five seconds have passed since the utterance began, but only a moment since the speaker
        // last made a sound — they are still talking.
        assembler.Close(T0.AddSeconds(5)).Should().BeNull();
        assembler.Close(T0.AddSeconds(5).AddMilliseconds(800)).Should().NotBeNull();
    }

    [Fact]
    public void SomethingTooShortToBeSpeechIsDropped()
    {
        UtteranceAssembler assembler = Assembler();
        assembler.Append(Audio(200), T0);

        assembler.Close(T0.AddSeconds(2)).Should().BeNull("200ms is a cough, not a sentence");
        assembler.IsCollecting.Should().BeFalse("it was still cleared away");
    }

    [Fact]
    public void TheMinimumIsInclusive()
    {
        UtteranceAssembler assembler = Assembler();
        assembler.Append(Audio(400), T0);

        assembler.Close(T0.AddSeconds(2)).Should().NotBeNull();
    }

    [Fact]
    public void AnUnbrokenTalkerIsCutAtTheCeiling()
    {
        var limits = Limits with { MaxDuration = TimeSpan.FromSeconds(1) };
        UtteranceAssembler assembler = Assembler(limits);

        assembler.Append(Audio(600), T0).Should().BeNull();
        VoiceUtterance? cut = assembler.Append(Audio(400), T0.AddMilliseconds(600));

        cut.Should().NotBeNull("the ceiling bounds what is held, and nothing else would end this");
        cut!.Duration.Should().Be(TimeSpan.FromSeconds(1));
        assembler.IsCollecting.Should().BeFalse();
    }

    [Fact]
    public void AudioAfterACeilingCutStartsAFreshUtterance()
    {
        var limits = Limits with { MaxDuration = TimeSpan.FromSeconds(1) };
        UtteranceAssembler assembler = Assembler(limits);
        assembler.Append(Audio(1000), T0);

        assembler.Append(Audio(500), T0.AddSeconds(1));
        VoiceUtterance? second = assembler.Close(T0.AddSeconds(2));

        second!.Duration.Should().Be(TimeSpan.FromMilliseconds(500));
        second.StartedAt.Should().Be(T0.AddSeconds(1), "the second utterance began when its audio did");
    }

    [Fact]
    public void ForcingTakesWhatIsThereWithoutWaitingOutTheGap()
    {
        // Somebody who left the channel is not coming back to finish their sentence.
        UtteranceAssembler assembler = Assembler();
        assembler.Append(Audio(500), T0);

        assembler.Close(T0, force: true).Should().NotBeNull();
    }

    [Fact]
    public void ForcingStillRefusesSomethingTooShortToBeSpeech()
    {
        UtteranceAssembler assembler = Assembler();
        assembler.Append(Audio(100), T0);

        assembler.Close(T0, force: true).Should().BeNull();
    }

    [Fact]
    public void ClosingWhenNobodySpokeProducesNothing()
    {
        Assembler().Close(T0, force: true).Should().BeNull();
    }

    [Fact]
    public void EmptyAudioIsNotTheStartOfAnUtterance()
    {
        UtteranceAssembler assembler = Assembler();

        assembler.Append([], T0).Should().BeNull();
        assembler.IsCollecting.Should().BeFalse();
    }

    [Fact]
    public void TheAudioHandedOverIsTheAudioThatWentIn()
    {
        // A buffer reused between utterances is how the second one arrives carrying the first one's
        // tail, which reads as a recogniser fault rather than a capture one.
        UtteranceAssembler assembler = Assembler();
        var first = new byte[16000];
        Array.Fill(first, (byte)7);

        assembler.Append(first, T0);
        VoiceUtterance? utterance = assembler.Close(T0.AddSeconds(2));

        utterance!.Audio.Should().Equal(first);

        assembler.Append(Audio(500), T0.AddSeconds(3));
        assembler.Close(T0.AddSeconds(5))!.Audio.Should().AllSatisfy(b => b.Should().Be(0));
    }
}
