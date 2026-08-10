using System.Text;

using FluentAssertions;

using KGSM.Bot.Discord.Commands;

using Xunit;

namespace KGSM.Bot.Core.Tests.Discord;

/// <summary>
/// Fitting a log into one attachment. The only interesting decision is which end survives.
/// </summary>
public sealed class LogsModuleTests
{
    private const int Limit = 7 * 1024 * 1024;

    [Fact]
    public void AShortLogIsPassedThroughWhole()
    {
        (string text, bool truncated) = LogsModule.Fit(["first", "second", "third"]);

        text.Should().Be("first\nsecond\nthird");
        truncated.Should().BeFalse();
    }

    [Fact]
    public void AnEmptyLogIsEmptyRatherThanTruncated()
    {
        (string text, bool truncated) = LogsModule.Fit([]);

        text.Should().BeEmpty();
        truncated.Should().BeFalse();
    }

    /// <summary>
    /// The end of a log is the part somebody asked for, so an oversized one loses its beginning. The
    /// opposite would hand back a file that stops before the thing being diagnosed.
    /// </summary>
    [Fact]
    public void AnOversizedLogKeepsTheNewestLines()
    {
        string[] log = [.. Enumerable.Range(0, 20_000).Select(i => new string('x', 1000) + i)];

        (string text, bool truncated) = LogsModule.Fit(log);

        truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(text).Should().BeLessThanOrEqualTo(Limit);
        text.Should().EndWith(log[^1]);
        text.Should().NotContain(log[0]);
    }

    /// <summary>
    /// Measured in bytes, because the limit is in bytes. A log of non-ASCII counted by characters
    /// would measure short here and be refused by Discord instead.
    /// </summary>
    [Fact]
    public void TheBudgetIsBytesNotCharacters()
    {
        // Four bytes per character in UTF-8, so a character count would think this is a quarter of
        // its real size.
        string[] log = [.. Enumerable.Range(0, 4000).Select(_ => string.Concat(Enumerable.Repeat("𝍖", 500)))];

        (string text, bool truncated) = LogsModule.Fit(log);

        truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(text).Should().BeLessThanOrEqualTo(Limit);
    }
}
