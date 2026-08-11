using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Infrastructure.Discord;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The four answers a health check can give, and why collapsing any two of them is how a health page
/// starts lying.
/// </summary>
/// <remarks>
/// <see cref="BotHealthService"/> itself is a composition of seven live dependencies — a gateway
/// client, two SQLite stores, a kgsm process and a socket — and standing all of those up would test
/// the substitutes rather than the service. What is pinned here is the vocabulary they are reported
/// in, which is the part that decides whether an operator reads the answer correctly.
/// </remarks>
public sealed class BotHealthServiceTests
{
    private static HealthCheck Check(HealthVerdict verdict, string name = "Something") =>
        new(name, verdict, "measured");

    /// <summary>
    /// <b>A dependency this host was never given is not a fault.</b> Counting an undeployed assistant
    /// against the total makes a correctly configured host read as permanently short of something,
    /// and an operator who chases it finds nothing wrong.
    /// </summary>
    [Fact]
    public void SomethingDeliberatelyNotConfiguredIsNotCountedAgainstTheHost()
    {
        string summary = HealthModule.Summary(
            [Check(HealthVerdict.Ok), Check(HealthVerdict.Ok), Check(HealthVerdict.Off)]);

        summary.Should().Contain("All 2 answering").And.Contain("1 not configured");
    }

    [Fact]
    public void AHostWithEverythingAnsweringSaysSoWithoutQualification()
    {
        HealthModule.Summary([Check(HealthVerdict.Ok), Check(HealthVerdict.Ok)])
            .Should().Be("All 2 answering.");
    }

    /// <summary>
    /// "Could not tell" is not a pass. A check that did not reach an answer is the one somebody most
    /// needs to notice, and folding it into the healthy count buries it.
    /// </summary>
    [Fact]
    public void AnUndeterminedCheckIsReportedSeparatelyFromAFailingOne()
    {
        string summary = HealthModule.Summary(
            [Check(HealthVerdict.Ok), Check(HealthVerdict.Failing), Check(HealthVerdict.Unknown)]);

        summary.Should().Contain("1 of 3 not answering").And.Contain("1 couldn't be determined");
    }

    [Fact]
    public void AnUndeterminedCheckAloneIsNotReportedAsAllAnswering()
    {
        HealthModule.Summary([Check(HealthVerdict.Ok), Check(HealthVerdict.Unknown)])
            .Should().NotContain("All").And.Contain("couldn't be determined");
    }

    /// <summary>
    /// Four verdicts, four markers. Two verdicts sharing a marker is the same collapse as two sharing
    /// a count, and it happens where nobody is looking — in the one glyph somebody actually reads.
    /// </summary>
    [Fact]
    public void EveryVerdictHasItsOwnMarker()
    {
        HealthVerdict[] verdicts = Enum.GetValues<HealthVerdict>();

        verdicts.Select(HealthModule.Marker).Should().OnlyHaveUniqueItems();
        verdicts.Select(HealthModule.Marker).Should().OnlyContain(m => !string.IsNullOrWhiteSpace(m));
    }

    [Theory]
    [InlineData(30, "less than a minute")]
    [InlineData(90, "2 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(86400, "1 day")]
    [InlineData(259200, "3 days")]
    public void AnAgeIsSaidInTheLargestUnitThatStillMeansSomething(int seconds, string expected)
    {
        BotHealthService.Age(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    /// <summary>
    /// A clock that has moved backwards between the journal's write and this read must not produce a
    /// negative age — "-3 hours old" reads as a bug in the bot rather than as the skew it is.
    /// </summary>
    [Fact]
    public void AnAgeIsNeverNegative()
    {
        BotHealthService.Age(TimeSpan.FromSeconds(-500)).Should().Be("less than a minute");
    }
}
