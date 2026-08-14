using FluentAssertions;

using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Speech;

using Xunit;

namespace KGSM.Bot.Core.Tests.Speech;

/// <summary>
/// The voices are files, and the name of one arrives from configuration, from a slash command, or
/// from the Control Panel. Reading the directory is what lets the bot offer the picker and validate a
/// choice with no synthesiser in the process — and what a name is checked against before it is ever
/// allowed near a path.
/// </summary>
/// <remarks>
/// These run against the voices the build copies beside the test binary, which are the same files the
/// deploy ships beside the bot.
/// </remarks>
public class InstalledVoicesTests
{
    [Fact]
    public void TheVoicesShippedBesideTheBinaryAreFound() =>
        InstalledVoices.All().Should().Contain("af_heart");

    [Fact]
    public void OnlyTheEnglishOnesAreOffered()
    {
        // Kokoro's other languages sit in the same tree and expect text in those languages. Offering
        // them would be twenty-odd ways to read an English answer badly.
        InstalledVoices.Offered().Should().OnlyContain(v => v[0] == 'a' || v[0] == 'b');
        InstalledVoices.Offered().Should().NotContain(v => v.StartsWith("zf_") || v.StartsWith("zm_"));
    }

    [Fact]
    public void TheOfferedListLeadsWithThePreferredOrder() =>
        InstalledVoices.Offered().First().Should().Be(SpeechVoices.Preferred[0]);

    [Fact]
    public void AVoiceThisHostHasResolvesToItsFile()
    {
        string? file = InstalledVoices.Find("af_heart");

        file.Should().NotBeNull();
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void TheNameIsMatchedHoweverItIsSpelled() =>
        InstalledVoices.Find("AF_Heart").Should().Be(InstalledVoices.Find("af_heart"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no_such_voice")]
    public void AVoiceThisHostDoesNotHaveIsNotFound(string name) =>
        InstalledVoices.Find(name).Should().BeNull();

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("voices-zh/../af_heart")]
    public void ANameIsNeverComposedIntoAPath(string mischief) =>
        // A voice name is configuration — it arrives from a settings file, an environment variable or
        // a slash command — and configuration that reaches the filesystem unchecked is how ".." gets
        // read as a voice. Matched against the listing, so only a real filename can ever come back.
        InstalledVoices.Find(mischief).Should().BeNull();
}
