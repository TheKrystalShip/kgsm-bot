using System.Text.Json;

using FluentAssertions;

using KGSM.Bot.Infrastructure.Assistant;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// How a tool call is described to somebody reading a Discord channel — and what is deliberately
/// never described.
/// </summary>
public sealed class AssistantToolVocabularyTests
{
    [Theory]
    [InlineData("read_console", "Read the console")]
    [InlineData("run_health_check", "Ran a health check")]
    [InlineData("search", "Searched the guides")]
    public void AKnownTool_ReadsAsWords(string tool, string expected) =>
        AssistantToolVocabulary.Label(tool).Should().Be(expected);

    /// <summary>
    /// The assistant's catalog grows without this repo being rebuilt. A tool nobody has written prose
    /// for is turned back into words rather than dropped — a step missing from the account of a turn
    /// is the one thing a transcript must not have.
    /// </summary>
    [Theory]
    [InlineData("trace_memory_pressure", "Trace memory pressure")]
    [InlineData("some-new-tool", "Some new tool")]
    public void AnUnknownTool_IsStillDescribed(string tool, string expected) =>
        AssistantToolVocabulary.Label(tool).Should().Be(expected);

    [Fact]
    public void ANamelessTool_StillReadsAsSomething() =>
        AssistantToolVocabulary.Label(string.Empty).Should().Be("Worked on it");

    // --- subjects -------------------------------------------------------------------------------

    [Fact]
    public void TheSubject_IsTheServerTheToolWasCalledOn() =>
        AssistantToolVocabulary.SubjectOf(new Dictionary<string, string?>
        {
            ["lines"] = "200",
            ["instance_name"] = "triageprobe",
        }).Should().Be("triageprobe");

    [Fact]
    public void WithNothingNamingATarget_ThereIsNoSubject() =>
        AssistantToolVocabulary.SubjectOf(new Dictionary<string, string?> { ["lines"] = "200" })
            .Should().BeNull();

    [Fact]
    public void WithNoArguments_ThereIsNoSubject() =>
        AssistantToolVocabulary.SubjectOf(null).Should().BeNull();

    // --- what a card is allowed to say ----------------------------------------------------------

    /// <summary>
    /// Confidence is printed only when it is not the ordinary case. Printed on every row, a reader
    /// stops reading the column precisely when it starts saying something.
    /// </summary>
    [Fact]
    public void ACardsConfidence_IsSaidOnlyWhenItQualifiesTheResult()
    {
        // `confirmed` is the assistant's word for a measured fact — the ordinary case, and noise on
        // every row. The two that mean a conclusion was inferred are exactly what a reader needs.
        Describe("""{"subject":{"id":"triageprobe"},"confidence":"confirmed"}""")
            .Should().Be("triageprobe");

        Describe("""{"subject":{"id":"triageprobe"},"confidence":"possible"}""")
            .Should().Be("triageprobe · confidence: possible");

        Describe("""{"subject":{"id":"triageprobe"},"confidence":"likely"}""")
            .Should().Be("triageprobe · confidence: likely");
    }

    /// <summary>
    /// A card naming what the row already names adds nothing. Printed anyway it reads as two facts
    /// where there is one — "Read the console — triageprobe (triageprobe)".
    /// </summary>
    [Fact]
    public void ACardRepeatingTheRowsSubject_DoesNotSayItTwice()
    {
        Describe("""{"subject":{"id":"triageprobe"},"confidence":"confirmed"}""", "triageprobe")
            .Should().BeNull();

        Describe("""{"subject":{"id":"triageprobe"},"confidence":"possible"}""", "triageprobe")
            .Should().Be("confidence: possible");
    }

    /// <summary>
    /// A card shape this build does not recognise says nothing rather than a guess. The assistant owns
    /// this vocabulary, and a surface inventing a reading of an unfamiliar payload is how a tool that
    /// found nothing comes to be described as one that found something.
    /// </summary>
    [Theory]
    [InlineData("""{"somethingElse":42}""")]
    [InlineData("""{"subject":{}}""")]
    [InlineData("""["not","an","object"]""")]
    public void AnUnfamiliarCard_SaysNothing(string json) =>
        Describe(json).Should().BeNull();

    [Fact]
    public void NoCardAtAll_SaysNothing() =>
        AssistantToolVocabulary.DescribeCard(null).Should().BeNull();

    private static string? Describe(string json, string? knownSubject = null)
    {
        using var document = JsonDocument.Parse(json);
        return AssistantToolVocabulary.DescribeCard(document.RootElement.Clone(), knownSubject);
    }
}
