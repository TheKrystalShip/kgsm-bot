using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// This decides whether the bot answers at all, and both ways of getting it wrong are bad in public:
/// missing the trigger looks like a bot that ignores people, and matching too eagerly is a bot that
/// interrupts a conversation it was only mentioned in.
/// </summary>
public class WakeWordDetectorTests
{
    private static WakeWordDetector Detector(params string[] triggers) =>
        new(triggers.Length == 0 ? ["hey assistant"] : triggers);

    [Fact]
    public void TheTriggerFollowedByARequestGivesTheRequest()
    {
        Detector().Match("Hey assistant, restart the factorio server")
            .Should().Be("restart the factorio server");
    }

    [Theory]
    [InlineData("Hey assistant, restart factorio", "restart factorio")]
    [InlineData("Hey, assistant! Restart factorio", "Restart factorio")]
    [InlineData("hey assistant restart factorio", "restart factorio")]
    [InlineData("HEY ASSISTANT. Restart factorio", "Restart factorio")]
    public void PunctuationAndCapitalisationInTheTriggerDoNotMatter(string transcript, string expected)
    {
        // A recogniser punctuates on prosody, so the same words come back differently depending on
        // how they were said. None of that is a different thing to have said — while the request
        // itself comes through exactly as it was recognised, which is why the four expectations here
        // are not all the same string.
        Detector().Match(transcript).Should().Be(expected, because: transcript);
    }

    [Fact]
    public void TheRequestKeepsItsOwnCapitalisation()
    {
        // Servers here have names like Ketchup. Lower-casing the request to match the trigger would
        // hand the assistant a different word than the person said.
        Detector().Match("Hey assistant, is Ketchup running?")
            .Should().Be("is Ketchup running");
    }

    [Fact]
    public void TheTriggerIsFoundAfterALeadIn()
    {
        // Measured against a real channel: "okay, let me try this - hey assistant, ..." is one breath
        // and therefore one utterance, and a rule that the trigger must come first refuses something
        // plainly addressed to the bot. Utterance boundaries are drawn by silence, which is a fact
        // about this pipeline rather than about how people talk.
        Detector().Match("Okay, that's enough of a pause. So, hey assistant, is Ketchup running?")
            .Should().Be("is Ketchup running");
    }

    [Fact]
    public void QuotingTheTriggerIsAnsweredAsThoughItWereAnInstruction()
    {
        // The accepted cost of finding the trigger anywhere, written down as a test so it is a
        // decision rather than a surprise. Quoting a wake word is rare; leading into a sentence is
        // what everyone does, and a bot that ignores genuine requests is broken in a way an
        // occasional spurious answer is not.
        Detector().Match("so I said hey assistant and nothing happened")
            .Should().Be("and nothing happened");
    }

    [Fact]
    public void MentioningTheBotWithoutTheTriggerIsNotAddressedToIt()
    {
        Detector().Match("I asked the assistant about that yesterday").Should().BeNull();
        Detector().Match("the assistant restarted it for me").Should().BeNull();
    }

    [Fact]
    public void SomebodyStartingAgainMeansTheLastTriggerTheySaid()
    {
        Detector().Match("Hey assistant, no wait. Hey assistant, restart factorio")
            .Should().Be("restart factorio");
    }

    [Fact]
    public void OrdinaryConversationIsNotAddressedToTheBot()
    {
        Detector().Match("did you see the base I built").Should().BeNull();
        Detector().Match("assistant").Should().BeNull("half the trigger is not the trigger");
    }

    [Fact]
    public void TheTriggerAloneIsAddressedToTheBotWithNothingAskedYet()
    {
        // Somebody getting the bot's attention before they have decided on the words. Empty is a
        // real answer and must be distinguishable from "not addressed", which is null.
        Detector().Match("Hey assistant").Should().BeEmpty();
        Detector().Match("Hey, assistant!").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingSaidIsNotAddressedToTheBot(string? transcript)
    {
        Detector().Match(transcript).Should().BeNull();
    }

    [Fact]
    public void AnyOfTheConfiguredTriggersWorks()
    {
        // The point of a list: an operator who sees the recogniser render the phrase a new way adds
        // that rendering here, with no model to retrain.
        WakeWordDetector detector = Detector("hey assistant", "hey kgsm", "okay assistant");

        detector.Match("Hey KGSM, stop minecraft").Should().Be("stop minecraft");
        detector.Match("Okay assistant, stop minecraft").Should().Be("stop minecraft");
        detector.Match("Hey there, stop minecraft").Should().BeNull();
    }

    [Fact]
    public void ALongerTriggerWinsOverAShorterOneItStartsWith()
    {
        // Matching "hey" alone would leave "assistant" sitting at the front of the request, and the
        // assistant would be asked to do something about the word "assistant".
        WakeWordDetector detector = Detector("hey", "hey assistant");

        detector.Match("Hey assistant, restart factorio").Should().Be("restart factorio");
    }

    [Fact]
    public void TheLatestTriggerWinsAcrossDifferentTriggers()
    {
        // Latest has to win across the whole trigger list rather than within each one: a long trigger
        // said early must not beat a short one said later, because the later one is what somebody
        // actually addressed to the bot.
        WakeWordDetector detector = Detector("hey assistant", "computer");

        detector.Match("hey assistant is what I usually say, but computer, restart factorio")
            .Should().Be("restart factorio");
    }

    [Fact]
    public void ASingleWordTriggerWorks()
    {
        Detector("assistant").Match("Assistant, what's running?").Should().Be("what's running");
    }

    [Fact]
    public void TrailingPunctuationIsNotPartOfTheRequest()
    {
        Detector().Match("Hey assistant, what is running?").Should().Be("what is running");
        Detector().Match("Hey assistant, restart it.").Should().Be("restart it");
    }

    [Fact]
    public void AQuotationMarkIsNotPartOfTheRequest()
    {
        // Measured: saying the trigger inside a quoted phrase leaves the closing quote on the end of
        // the request, where it reaches the assistant attached to a server's name.
        Detector().Match("with the lead in phrase \"Hey assistant, is Minecraft running?\"")
            .Should().Be("is Minecraft running");
    }

    [Fact]
    public void AnApostropheEndingARealWordSurvives()
    {
        // The reason the apostrophe is not trimmed with the rest of the punctuation.
        Detector().Match("Hey assistant, whose server is it").Should().Be("whose server is it");
        Detector("assistant").Match("assistant, what's running").Should().Be("what's running");
    }

    [Fact]
    public void ATriggerWrittenWithPunctuationStillMatches()
    {
        // The operator types the phrase; they should not have to know how it is normalised.
        Detector("Hey, Assistant!").Match("hey assistant restart factorio")
            .Should().Be("restart factorio");
    }

    [Fact]
    public void AnEmptyTriggerListMatchesNothing()
    {
        // A misconfigured host must be silent rather than answering everything said in the room.
        new WakeWordDetector([]).Match("Hey assistant, restart factorio").Should().BeNull();
        new WakeWordDetector(["", "  "]).Match("anything at all").Should().BeNull();
    }
}
