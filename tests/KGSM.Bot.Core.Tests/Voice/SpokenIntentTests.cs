using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// Reading a spoken yes or no about something destructive.
/// </summary>
/// <remarks>
/// The tests that matter here are the ones asserting <see cref="SpokenIntent.Unclear"/>. Approving is
/// the outcome that cannot be undone, so every case where this must NOT approve is worth more than
/// any case where it should — a gap in the vocabulary costs one repeated question, and a false
/// affirmative costs a server.
/// </remarks>
public class SpokenIntentTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("yeah")]
    [InlineData("yep")]
    [InlineData("yup")]
    [InlineData("sure")]
    [InlineData("okay")]
    [InlineData("ok")]
    [InlineData("alright")]
    [InlineData("do it")]
    [InlineData("go ahead")]
    [InlineData("go for it")]
    [InlineData("send it")]
    [InlineData("make it so")]
    [InlineData("sounds good")]
    [InlineData("please do")]
    [InlineData("yes please")]
    [InlineData("of course")]
    [InlineData("absolutely")]
    [InlineData("definitely")]
    [InlineData("confirm")]
    [InlineData("proceed")]
    [InlineData("that works")]
    [InlineData("why not")]
    [InlineData("you bet")]
    [InlineData("roger")]
    [InlineData("aye")]
    [InlineData("let's do it")]
    [InlineData("go ahead and do it")]
    [InlineData("yeah go ahead")]
    [InlineData("yes, do it")]
    [InlineData("uh, yeah")]
    [InlineData("Yes.")]
    [InlineData("YEP!")]
    [InlineData("ok do that")]
    [InlineData("sure thing")]
    [InlineData("yeah do it please")]
    public void PeopleAgreeInManyWaysAndAllOfThemCount(string said)
    {
        SpokenIntents.Read(said).Should().Be(SpokenIntent.Affirm);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("nope")]
    [InlineData("nah")]
    [InlineData("cancel")]
    [InlineData("don't")]
    [InlineData("dont do it")]
    [InlineData("no don't")]
    [InlineData("never mind")]
    [InlineData("forget it")]
    [InlineData("leave it")]
    [InlineData("not now")]
    [InlineData("not yet")]
    [InlineData("hold on")]
    [InlineData("wait")]
    [InlineData("abort")]
    [InlineData("no thanks")]
    [InlineData("actually no")]
    [InlineData("no, leave it alone")]
    [InlineData("stop")]
    [InlineData("scratch that")]
    public void AndTheyDeclineInAsMany(string said)
    {
        SpokenIntents.Read(said).Should().Be(SpokenIntent.Decline);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("probably")]
    [InlineData("i think so")]
    [InlineData("yeah maybe")]
    [InlineData("i guess so")]
    [InlineData("not sure")]
    [InlineData("i don't know")]
    [InlineData("up to you")]
    [InlineData("either way")]
    public void AHedgeIsNotConsentHoweverItLeans(string said)
    {
        // "Probably" is the answer of somebody who has not decided. Reading it as yes is deciding
        // for them, about something that cannot be undone.
        SpokenIntents.Read(said).Should().Be(SpokenIntent.Unclear);
    }

    [Theory]
    [InlineData("yeah I was telling him about the minecraft thing")]
    [InlineData("no way, did you see that base he built")]
    [InlineData("yes but the other one keeps crashing on me")]
    [InlineData("ok so anyway what were you saying about the update")]
    [InlineData("sure, and then he just walked straight into the lava")]
    public void TalkingToSomebodyElseIsNotAnAnswer(string said)
    {
        // The measured hazard: the window is open, the person turns to a friend, and their sentence
        // happens to start with "yeah". Length is what separates an answer from a conversation.
        SpokenIntents.Read(said).Should().Be(SpokenIntent.Unclear);
    }

    [Theory]
    [InlineData("yeah no")]
    [InlineData("no yeah")]
    [InlineData("yes, no wait")]
    [InlineData("ok no")]
    public void SayingBothIsSayingNeither(string said)
    {
        SpokenIntents.Read(said).Should().Be(SpokenIntent.Unclear);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("what")]
    [InlineData("hmm")]
    [InlineData("minecraft")]
    [InlineData("the other server")]
    [InlineData("can you repeat that")]
    public void NothingRecognisableIsNeverApproval(string? said)
    {
        SpokenIntents.Read(said).Should().Be(SpokenIntent.Unclear);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("yesterday")]
    [InlineData("nobody")]
    [InlineData("notice")]
    [InlineData("okra")]
    [InlineData("surely not")]
    public void AWordThatMerelyContainsAYesOrANoIsNeither(string said)
    {
        // "Running" contains "no" and "yesterday" contains "yes". Matching on substrings would read
        // half of English as a decision.
        SpokenIntents.Read(said).Should().NotBe(SpokenIntent.Affirm);
    }

    [Fact]
    public void ADeclineThatEmbedsAnAgreementIsStillADecline()
    {
        // "Don't do it" contains "do it". The refusal is the specific phrase and wins.
        SpokenIntents.Read("don't do it").Should().Be(SpokenIntent.Decline);
        SpokenIntents.Read("no, don't do that").Should().Be(SpokenIntent.Decline);
    }

    [Fact]
    public void ALongerNoIsStillAllowedToBeNo()
    {
        // The two directions are judged differently on purpose: a wrongly-read no costs a repeat,
        // and a wrongly-read yes cannot be taken back.
        SpokenIntents.Read("no, I meant the other server").Should().Be(SpokenIntent.Decline);
        SpokenIntents.Read("yes, I meant the other server").Should().Be(SpokenIntent.Unclear);
    }

    [Theory]
    [InlineData("uh huh")]
    [InlineData("mhm")]
    [InlineData("mm hmm")]
    [InlineData("uh uh")]
    public void AGruntIsNeverApproval(string said)
    {
        // "Uh-huh" is yes and "uh-uh" is no, one vowel apart, over a noisy voice channel. Only words
        // approve — a recogniser that gets these backwards gets them backwards toward approving.
        SpokenIntents.Read(said).Should().Be(SpokenIntent.Unclear);
    }
}
