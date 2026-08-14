using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// What counts as asking the bot to forget, and — the half that matters — what does not. This runs
/// before a turn is dispatched, so a false positive discards a room's conversation instead of
/// answering the question somebody asked.
/// </summary>
public class SpokenConversationCommandTests
{
    [Theory]
    [InlineData("start over")]
    [InlineData("Start over.")]
    [InlineData("start again")]
    [InlineData("start a new conversation")]
    [InlineData("clear the conversation")]
    [InlineData("reset the conversation")]
    [InlineData("forget everything")]
    [InlineData("forget all that")]
    [InlineData("wipe the conversation")]
    public void TheWaysPeopleAskForABlankSlate(string said) =>
        SpokenConversationCommands.Read(said).Should().Be(SpokenConversationCommand.Clear);

    [Theory]
    [InlineData("please start over")]
    [InlineData("can you start over")]
    [InlineData("okay, let's start over")]
    public void PolitenessAroundItStillCounts(string said) =>
        SpokenConversationCommands.Read(said).Should().Be(SpokenConversationCommand.Clear);

    [Theory]
    [InlineData("compact the conversation")]
    [InlineData("summarise our conversation")]
    [InlineData("summarize the conversation")]
    public void AskingForTheGentlerOne(string said) =>
        SpokenConversationCommands.Read(said).Should().Be(SpokenConversationCommand.Compact);

    [Theory]
    [InlineData("restart the minecraft server")]
    [InlineData("start terraria")]
    [InlineData("what's running")]
    [InlineData("")]
    [InlineData("   ")]
    public void OrdinaryRequestsAreNotCommands(string said) =>
        SpokenConversationCommands.Read(said).Should().Be(SpokenConversationCommand.None);

    [Fact]
    public void AStopFailingIsNotAnAskToStartOver()
    {
        // ⚠ The dangerous near-miss: the words are in there, and the sentence is a question about a
        // server. Matching on containment would answer it by wiping the room.
        SpokenConversationCommands.Read("the server didn't start over the weekend, can you check")
            .Should().Be(SpokenConversationCommand.None);
    }

    [Fact]
    public void ALongSentenceContainingThePhraseIsARequest()
    {
        SpokenConversationCommands.Read(
                "if the valheim server is still down then start over from the last backup please")
            .Should().Be(SpokenConversationCommand.None);
    }

    [Fact]
    public void AskingForBothLeansTowardKeeping()
    {
        // Folding what was said keeps it; discarding it does not. When an utterance reads as both, the
        // recoverable one is the right way to be wrong.
        SpokenConversationCommands.Read("compact the conversation")
            .Should().Be(SpokenConversationCommand.Compact);
    }

    [Fact]
    public void ForgetItIsNotForgetEverything()
    {
        // "Forget it" is overwhelmingly "never mind about that", said after the bot misunderstands —
        // the exact moment somebody would be most annoyed to lose the whole conversation.
        SpokenConversationCommands.Read("forget it").Should().Be(SpokenConversationCommand.None);
    }
}
