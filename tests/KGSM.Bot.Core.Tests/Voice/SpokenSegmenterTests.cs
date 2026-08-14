using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// A reply arrives from the assistant as token fragments and is spoken a sentence at a time, so the
/// first one goes out while the model is still writing the rest. What must hold: the whole reply is
/// said, in order, and a fenced block is never cut into lines to be read out one by one.
/// </summary>
public class SpokenSegmenterTests
{
    /// <summary>Everything a segmenter produced for a reply, in order.</summary>
    private static List<string> Read(params string[] slices)
    {
        var segmenter = new SpokenSegmenter();
        var said = new List<string>();

        foreach (string slice in slices)
            said.AddRange(segmenter.Wrote(slice));

        if (segmenter.Rest() is { Length: > 0 } rest) said.Add(rest);

        return said;
    }

    /// <summary>What was spoken, as one string — the whole reply as a listener hears it.</summary>
    private static string Heard(params string[] slices) => string.Join(" ", Read(slices));

    [Fact]
    public void NothingIsSaidUntilThereIsAWholeSentence()
    {
        var segmenter = new SpokenSegmenter();

        segmenter.Wrote("Factorio").Should().BeEmpty();
        segmenter.Wrote(" is ru").Should().BeEmpty();
        segmenter.Wrote("nning.").Should().BeEmpty();
    }

    [Fact]
    public void ASentenceIsSpokenAsSoonAsItIsFinished()
    {
        var segmenter = new SpokenSegmenter();

        // Long enough to be worth its own recital, and followed by the start of the next one — which
        // is what says the full stop really ended a sentence.
        segmenter.Wrote("Factorio is running and has been up for eleven days. It has")
            .Should().ContainSingle()
            .Which.Should().Be("Factorio is running and has been up for eleven days.");
    }

    [Fact]
    public void ATerminatorAtTheVeryEndOfWhatHasArrivedIsNotYetABoundary()
    {
        // Nothing after it says whether it ended a sentence or sits inside a number. The next slice
        // decides, and the flush covers a reply that simply stopped there.
        var segmenter = new SpokenSegmenter();

        segmenter.Wrote("Factorio is running and has been up for eleven days.").Should().BeEmpty();
        segmenter.Wrote(" ").Should().ContainSingle();
    }

    [Fact]
    public void AVersionNumberIsNotASentenceEnd()
    {
        Heard("The host is running version 2.0.58 of the engine and everything looks healthy.")
            .Should().Be("The host is running version 2.0.58 of the engine and everything looks healthy.");
    }

    [Fact]
    public void AShortSentenceRidesOutWithTheOneAfterIt()
    {
        // "Yes." on its own is a recital of one syllable with a synthesis call and a hand-off around
        // it, and a run of those is worse to listen to than one sentence.
        Read("Yes. It came back up on its own about four minutes ago, so nothing needs doing.")
            .Should().ContainSingle()
            .Which.Should().StartWith("Yes. It came back up");
    }

    [Fact]
    public void TheWholeReplyIsSpokenAndNothingIsAddedOrDropped()
    {
        const string reply =
            "Factorio is running and has been up for eleven days without a restart. "
            + "Terraria is stopped, and it was stopped deliberately rather than by a crash. "
            + "Nothing else on this host needs attention right now.";

        // Sliced the way tokens actually arrive — mid-word, at no boundary anybody chose.
        string[] slices = [.. Chunks(reply, 7)];

        Read(slices).Should().HaveCountGreaterThan(1);
        Heard(slices).Should().Be(reply.TrimEnd());
    }

    [Fact]
    public void ASentenceThatNeverEndsIsStillSpoken()
    {
        // A reply cut off by the model, or one that simply has no full stop. Leaving it unsaid would
        // have the room hear less than the channel shows.
        Read("The engine says the instance is unavailable and I could not read why")
            .Should().ContainSingle()
            .Which.Should().Be("The engine says the instance is unavailable and I could not read why");
    }

    [Fact]
    public void AFencedBlockIsNeverCutIntoLines()
    {
        // ⚠ The trap. Segmenting on newlines first and stripping markup per piece would read the
        // contents of the fence out a line at a time — the exact thing SpokenText exists to prevent.
        const string reply =
            "It crashed on start, and here is the tail of the log for you to look at:\n"
            + "```\n"
            + "Segfault at 0x0.\n"
            + "at main()!\n"
            + "at start()?\n"
            + "```\n"
            + "I would restart it and see whether it happens again.";

        List<string> said = Read([.. Chunks(reply, 5)]);

        said.Should().NotContain(s => s.Contains("Segfault", StringComparison.Ordinal));
        said.Should().NotContain(s => s.Contains("main()", StringComparison.Ordinal));
        string.Join(" ", said).Should().Be(
            "It crashed on start, and here is the tail of the log for you to look at: "
            + "I would restart it and see whether it happens again.");
    }

    [Fact]
    public void AFenceTheReplyNeverClosesIsDroppedAtTheFlush()
    {
        const string reply =
            "Here is the configuration it is running with right now, as it stands on disk:\n"
            + "```ini\n"
            + "enabled=true\n"
            + "port=34197\n";

        List<string> said = Read([.. Chunks(reply, 6)]);

        said.Should().NotContain(s => s.Contains("34197", StringComparison.Ordinal));
        string.Join(" ", said).Should().Be(
            "Here is the configuration it is running with right now, as it stands on disk:");
    }

    [Fact]
    public void NothingIsSpokenWhileAFenceIsStillOpen()
    {
        var segmenter = new SpokenSegmenter();

        segmenter.Wrote("Here is the configuration it is running with right now, on disk:\n```\n");
        segmenter.Wrote("enabled=true. port=34197. name=heisen's server. seed=0.\n")
            .Should().BeEmpty("a boundary inside a fence would leave its contents to be read out");
    }

    [Fact]
    public void ALineEndingIsABoundaryToo()
    {
        // A list item carries no full stop, and waiting for one would hold the whole list back.
        Read(
            "Here is what is on this host at the moment:\n",
            "- factorio, which is running and has eleven players on it\n",
            "- terraria, which is stopped\n")
            .Should().SatisfyRespectively(
                first => first.Should().Be("Here is what is on this host at the moment:"),
                second => second.Should().Be(
                    "factorio, which is running and has eleven players on it"),
                third => third.Should().Be("terraria, which is stopped"));
    }

    [Fact]
    public void MarkupIsStrippedFromEveryPiece()
    {
        Heard("**Factorio** is running on `port 34197` and it has been up for eleven days. ",
              "*Terraria* is stopped and nothing is wrong with it.")
            .Should().Be(
                "Factorio is running on port 34197 and it has been up for eleven days. "
                + "Terraria is stopped and nothing is wrong with it.");
    }

    [Fact]
    public void AnEmptyReplySaysNothing()
    {
        var segmenter = new SpokenSegmenter();

        segmenter.Wrote(null).Should().BeEmpty();
        segmenter.Wrote(string.Empty).Should().BeEmpty();
        segmenter.Rest().Should().BeEmpty();
    }

    [Fact]
    public void TheFlushEmptiesTheSegmenter()
    {
        // A second call must not repeat what has already been spoken.
        var segmenter = new SpokenSegmenter();

        segmenter.Wrote("A short answer");
        segmenter.Rest().Should().Be("A short answer");
        segmenter.Rest().Should().BeEmpty();
    }

    [Fact]
    public void WhitespaceAloneIsNotWorthSaying()
    {
        var segmenter = new SpokenSegmenter();

        segmenter.Wrote("\n\n   \n").Should().BeEmpty();
        segmenter.Rest().Should().BeEmpty();
    }

    private static IEnumerable<string> Chunks(string text, int size)
    {
        for (int i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
