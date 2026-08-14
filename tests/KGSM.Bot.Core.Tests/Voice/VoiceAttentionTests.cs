using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// When the bot decides it is waiting on somebody, and the bookkeeping that keeps that window shut
/// the rest of the time.
/// </summary>
public class VoiceAttentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 16, 0, 0, TimeSpan.Zero);

    private readonly VoiceAttention _attention = new();

    private static VoiceWaiting Waiting(DateTimeOffset until) =>
        new(VoiceWaitingFor.Answer, until);

    [Fact]
    public void NobodyIsWaitedOnByDefault()
    {
        _attention.Take(speakerId: 1, channelId: 9, Now).Should().BeNull();
    }

    [Fact]
    public void SomebodyWaitedOnIsLetThrough()
    {
        _attention.Expect(1, 9, Waiting(Now.AddSeconds(20)));

        _attention.Take(1, 9, Now).Should().NotBeNull();
    }

    [Fact]
    public void TheWindowIsSpentByOneUtterance()
    {
        _attention.Expect(1, 9, Waiting(Now.AddSeconds(20)));

        _attention.Take(1, 9, Now).Should().NotBeNull();
        _attention.Take(1, 9, Now).Should().BeNull("the answer was already given");
    }

    [Fact]
    public void AWindowThatRanOutIsNotAWindow()
    {
        _attention.Expect(1, 9, Waiting(Now.AddSeconds(20)));

        _attention.Take(1, 9, Now.AddSeconds(21)).Should().BeNull();
    }

    [Fact]
    public void ItBelongsToOneSpeakerInOneChannel()
    {
        _attention.Expect(1, 9, Waiting(Now.AddSeconds(20)));

        _attention.Take(2, 9, Now).Should().BeNull("somebody else was asked");
        _attention.Take(1, 5, Now).Should().BeNull("that was a different channel");
        _attention.Take(1, 9, Now).Should().NotBeNull();
    }

    [Fact]
    public void AWindowCanBeClosedWithoutBeingUsed()
    {
        _attention.Expect(1, 9, Waiting(Now.AddSeconds(20)));
        _attention.Forget(1, 9);

        _attention.Take(1, 9, Now).Should().BeNull();
    }

    [Theory]
    [InlineData("Which one did you mean, minecraft or necesse?")]
    [InlineData("It's stopped. Do you want me to start it?")]
    [InlineData("**Shall I back it up first?**")]
    [InlineData("Do you mean the one on port 27015? ")]
    public void AReplyEndingInAQuestionExpectsAnAnswer(string reply)
    {
        VoiceAttention.InvitesAnAnswer(reply).Should().BeTrue();
    }

    [Theory]
    [InlineData("No, it is stopped.")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("You asked whether it was running? It is.")]
    [InlineData("I've put a confirmation in the chat for you to approve.")]
    public void AnythingElseDoesNot(string? reply)
    {
        // The middle case matters: a reply that asks something and then answers it is not waiting on
        // anybody, so only the last sentence is read.
        VoiceAttention.InvitesAnAnswer(reply).Should().BeFalse();
    }
}
