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
    /// Cancel lives outside the confirm prefix. The confirm handler matches <c>kgsmact~*</c> on a
    /// wildcard, so a cancel id under that prefix would be captured by it and read as a grant —
    /// dismissing a prompt would become an attempt to redeem one.
    /// </summary>
    [Fact]
    public void CancelIsNotCapturedByTheConfirmWildcard()
    {
        AssistantConfirmationIds.Cancel.Should().NotStartWith(AssistantConfirmationIds.ConfirmPrefix);
        AssistantConfirmationIds.Cancel.Should().NotBe(AssistantConfirmationIds.ConfirmPrefix);
        AssistantConfirmationIds.Cancel.Length.Should()
            .BeLessThanOrEqualTo(AssistantConfirmationIds.MaxCustomIdLength);
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
