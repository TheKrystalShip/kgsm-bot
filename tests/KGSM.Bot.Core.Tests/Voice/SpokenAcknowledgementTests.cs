using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// The short thing said while the real work happens, and which jobs are worth warning about.
/// </summary>
public class SpokenAcknowledgementTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("update")]
    [InlineData("backup")]
    [InlineData("restore")]
    [InlineData("install_server")]
    [InlineData("Uninstall")]
    public void TheJobsWorthWarningAboutAreTheOnesThatMoveFilesAround(string kind)
    {
        // A confirmed install was measured at thirty-two seconds. Somebody who is told nothing
        // assumes it failed and says it again.
        SpokenAcknowledgement.IsSlow(kind).Should().BeTrue();
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("server_command")]
    [InlineData(null)]
    [InlineData("")]
    public void StartingAndStoppingAreNot(string? kind)
    {
        // These answer quickly enough that a warning about the wait would take longer than the wait.
        SpokenAcknowledgement.IsSlow(kind).Should().BeFalse();
    }

    [Fact]
    public void ALongJobSaysItWillTakeAWhileAndAShortOneDoesNot()
    {
        var longOnes = new HashSet<string>();
        var shortOnes = new HashSet<string>();

        // Sampled rather than called once: the phrase is chosen at random, and asserting on one
        // draw would pass while most of the list said the wrong thing.
        for (var i = 0; i < 200; i++)
        {
            longOnes.Add(SpokenAcknowledgement.WhileWorking("install"));
            shortOnes.Add(SpokenAcknowledgement.WhileWorking("start"));
        }

        longOnes.Should().OnlyContain(p => Mentions(p, "while") || Mentions(p, "bit")
            || Mentions(p, "minutes") || Mentions(p, "long") || Mentions(p, "finish"));
        shortOnes.Should().NotIntersectWith(longOnes);
    }

    [Fact]
    public void NothingSaidIsEverEmpty()
    {
        // It is spoken, and synthesising an empty string produces no audio and no acknowledgement —
        // which is the silence this exists to remove.
        for (var i = 0; i < 200; i++)
        {
            SpokenAcknowledgement.WhileThinking().Should().NotBeNullOrWhiteSpace();
            SpokenAcknowledgement.WhileWorking("install").Should().NotBeNullOrWhiteSpace();
            SpokenAcknowledgement.WhileWorking(null).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ItDoesNotSayTheSameThingEveryTime()
    {
        // A phrase that never changes stops being heard as a reply and starts being heard as a noise
        // the bot makes.
        var heard = new HashSet<string>();
        for (var i = 0; i < 200; i++) heard.Add(SpokenAcknowledgement.WhileThinking());

        heard.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void WhatItSaysIsPlainSpeechWithNothingASynthesiserWouldReadOut()
    {
        for (var i = 0; i < 200; i++)
        {
            foreach (string phrase in new[]
                     {
                         SpokenAcknowledgement.WhileThinking(),
                         SpokenAcknowledgement.WhileWorking("install"),
                         SpokenAcknowledgement.WhileWorking("start"),
                     })
            {
                phrase.Should().NotContainAny("*", "_", "`", "#", "[", "]", "(", ")", "http");
            }
        }
    }

    private static bool Mentions(string phrase, string word) =>
        phrase.Contains(word, StringComparison.OrdinalIgnoreCase);
}
