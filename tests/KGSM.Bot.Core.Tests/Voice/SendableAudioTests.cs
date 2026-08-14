using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// The rule that keeps short audio from wedging a voice connection.
/// </summary>
/// <remarks>
/// The failure this prevents has no symptom: the write neither returns nor throws, so the request
/// that started it never finishes, and because spoken requests are answered one at a time every later
/// one queues behind it. The bot stays connected, keeps hearing people, and answers nobody — with
/// nothing in the log. It is worth pinning precisely because it cannot be noticed.
/// </remarks>
public class SendableAudioTests
{
    /// <summary>48 kHz stereo signed 16-bit is 192 bytes per millisecond.</summary>
    private static byte[] Audio(int milliseconds) => new byte[milliseconds * 192];

    [Fact]
    public void TheFloorIsPastTheBufferTheWriterWaitsToFill()
    {
        // A write sized exactly to the boundary can arrive one frame short, because the encoder holds
        // a part-frame back instead of forwarding it.
        SendableAudio.PreloadBytes
            .Should().BeGreaterThan(SendableAudio.BufferMillis * 192);
    }

    [Fact]
    public void TheFloorIsAWholeNumberOfFrames()
    {
        // The writer counts in frames. A floor that is not a whole number of them is a floor that
        // does not mean what it says.
        (SendableAudio.PreloadBytes % SendableAudio.FrameBytes).Should().Be(0);
    }

    [Fact]
    public void AToneTooShortToSendIsPaddedUntilItWillGo()
    {
        // The measured case: 290ms against a one-second buffer.
        byte[] tone = Audio(290);

        byte[] sendable = SendableAudio.AtLeastPreload(tone);

        sendable.Length.Should().Be(SendableAudio.PreloadBytes);
    }

    [Fact]
    public void ThePaddingIsSilenceAndTheSoundIsUntouched()
    {
        var tone = new byte[Audio(290).Length];
        Array.Fill(tone, (byte)9);

        byte[] sendable = SendableAudio.AtLeastPreload(tone);

        sendable[..tone.Length].Should().Equal(tone, "the sound must play exactly as it was rendered");
        sendable[tone.Length..].Should().AllSatisfy(b => b.Should().Be(0), "zeroes are silence");
    }

    [Fact]
    public void AnAnswerLongEnoughToSendIsHandedOnUntouched()
    {
        // Every spoken answer is seconds long, and copying one to no purpose is work on the path
        // somebody is waiting on.
        byte[] answer = Audio(4000);

        SendableAudio.AtLeastPreload(answer).Should().BeSameAs(answer);
    }

    [Fact]
    public void AShortSpokenAnswerIsPaddedToo()
    {
        // Not only tones. "Yes." synthesises to well under a second, and would wedge the stream in
        // exactly the same way — the rule belongs to the writer, not to what is being said.
        SendableAudio.AtLeastPreload(Audio(600))
            .Length.Should().Be(SendableAudio.PreloadBytes);
    }
}
