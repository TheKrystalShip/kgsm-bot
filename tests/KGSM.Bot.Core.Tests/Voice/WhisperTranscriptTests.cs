using FluentAssertions;

using KGSM.Bot.Infrastructure.Discord.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// Whisper describes sound that is not speech instead of returning nothing for it, and a channel
/// with four people in it produces far more of that than it does words. Measured on a real one:
/// three of the five things recognised were <c>[BLANK_AUDIO]</c>.
/// </summary>
public class WhisperTranscriptTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("[ Silence ]")]
    [InlineData("[MUSIC PLAYING]")]
    [InlineData("(wind blowing)")]
    [InlineData("[BLANK_AUDIO] [BLANK_AUDIO]")]
    public void SoundThatIsNotSpeechComesBackEmpty(string raw)
    {
        // Left in, these reach the matcher as though they were words — and a bare trigger's follow-up
        // window would be spent by a cough, handing "[BLANK_AUDIO]" to the assistant as a request.
        WhisperSpeechToText.Spoken(raw).Should().BeEmpty();
    }

    [Fact]
    public void WordsSurviveTheAnnotationAroundThem()
    {
        WhisperSpeechToText.Spoken("[BLANK_AUDIO] Hey assistant, restart factorio")
            .Should().Be("Hey assistant, restart factorio");

        WhisperSpeechToText.Spoken("Hey assistant (coughs), restart factorio")
            .Should().Be("Hey assistant , restart factorio");
    }

    [Fact]
    public void OrdinarySpeechIsUntouched()
    {
        WhisperSpeechToText.Spoken(" Hey assistant, is stationeers running? ")
            .Should().Be("Hey assistant, is stationeers running?");
    }

    [Fact]
    public void AnUnclosedAnnotationDoesNotSwallowTheRestOfTheSentence()
    {
        // Whisper's bracketing is not guaranteed balanced, and a stray opener that ate everything
        // after it would silently drop the request while looking like a clean transcript.
        WhisperSpeechToText.Spoken("Hey assistant, restart factorio]")
            .Should().Be("Hey assistant, restart factorio");
    }

    [Fact]
    public void NothingAtAllIsEmptyRatherThanAFailure()
    {
        WhisperSpeechToText.Spoken("").Should().BeEmpty();
        WhisperSpeechToText.Spoken("   ").Should().BeEmpty();
    }
}
