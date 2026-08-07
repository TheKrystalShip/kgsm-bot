using FluentAssertions;

using KGSM.Bot.Discord.Commands;

using Xunit;

namespace KGSM.Bot.Tests.Discord;

/// <summary>
/// The button id for an assistant-staged action: it carries the assistant's grant and nothing else.
/// </summary>
public class AssistantConfirmationIdsTests
{
    // The shape the assistant issues: 32 hex characters, whatever the operation is.
    private const string Grant = "60c24e5b21a7c863fae9648b996ae116";

    [Fact]
    public void TheButtonCarriesTheGrantVerbatim()
    {
        var id = AssistantConfirmationIds.Confirm(Grant);

        id.Should().Be("kgsmact~" + Grant);
        id.Length.Should().BeLessThanOrEqualTo(AssistantConfirmationIds.MaxCustomIdLength);
    }

    /// <summary>
    /// Its own prefix, so a button can only ever be answered by the half of the bot that knows what
    /// to do with it — the ids the bot mints for actions it runs itself parse nothing from here.
    /// </summary>
    [Fact]
    public void ItDoesNotShareAPrefixWithTheBotsOwnConfirmations()
    {
        AssistantConfirmationIds.ConfirmPrefix.Should().NotBe(KGSM.Bot.Discord.Llm.ConfirmationIds.ConfirmPrefix);
        AssistantConfirmationIds.Confirm(Grant).Should()
            .NotStartWith(KGSM.Bot.Discord.Llm.ConfirmationIds.ConfirmPrefix);
    }

    /// <summary>
    /// A grant that would not fit is reported rather than truncated: a truncated grant builds a
    /// button Discord accepts and the assistant then refuses, which fails later and less clearly.
    /// </summary>
    [Theory]
    [InlineData(Grant, true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AGrantIsCheckedAgainstTheCapBeforeAButtonIsBuilt(string? grant, bool fits)
    {
        AssistantConfirmationIds.Fits(grant!).Should().Be(fits);
    }

    [Fact]
    public void AGrantThatOutgrewTheCapDoesNotFit()
    {
        var oversized = new string('a', AssistantConfirmationIds.MaxCustomIdLength);

        AssistantConfirmationIds.Fits(oversized).Should().BeFalse();
        // One character under the limit is the last one that does.
        AssistantConfirmationIds
            .Fits(new string('a', AssistantConfirmationIds.MaxCustomIdLength - AssistantConfirmationIds.ConfirmPrefix.Length))
            .Should().BeTrue();
    }
}
