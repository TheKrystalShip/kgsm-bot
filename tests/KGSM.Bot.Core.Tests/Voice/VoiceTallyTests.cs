using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// The reading given to somebody whose only symptom is that the bot said nothing.
/// </summary>
public class VoiceTallyTests
{
    private readonly VoiceTally _tally = new();

    private void Heard(int times)
    {
        for (var i = 0; i < times; i++) _tally.Heard();
    }

    [Fact]
    public void AWorkingSurfaceIsNotDiagnosed()
    {
        // A diagnosis printed beside numbers that are fine is noise, and trains people to skip it.
        Heard(10);
        for (var i = 0; i < 10; i++) _tally.Recognised();
        for (var i = 0; i < 3; i++) { _tally.Addressed(); _tally.Answered(); }

        _tally.Read().Diagnosis.Should().BeNull();
    }

    [Fact]
    public void TooLittleToGoOnIsNotDiagnosedEither()
    {
        // Two utterances that recognised nothing is a cough and a chair. Calling that a broken model
        // sends somebody to check a model that is fine.
        Heard(2);

        _tally.Read().Diagnosis.Should().BeNull();
    }

    [Fact]
    public void NothingRecognisedPointsAtTheModel()
    {
        Heard(10);

        _tally.Read().Diagnosis.Should().Contain("model");
    }

    [Fact]
    public void RecognisedButNeverAddressedPointsAtTheTriggerPhrase()
    {
        // The measured failure this whole tally exists for: whisper working perfectly, the trigger
        // never matching, and no way to tell the two apart from inside a voice channel.
        Heard(10);
        for (var i = 0; i < 10; i++) _tally.Recognised();

        _tally.Read().Diagnosis.Should().Contain("trigger");
    }

    [Fact]
    public void AddressedButNeverAnsweredPointsAtPeopleBeingCutOff()
    {
        Heard(10);
        for (var i = 0; i < 10; i++) { _tally.Recognised(); _tally.Addressed(); }

        _tally.Read().Diagnosis.Should().Contain("cut off");
    }

    [Fact]
    public void TheEarlierFailureIsTheOneReported()
    {
        // Nothing recognised means nothing addressed and nothing answered too. Reporting the last of
        // those would send somebody to tune a trigger phrase against silence.
        Heard(10);

        _tally.Read().Diagnosis.Should().Contain("model").And.NotContain("trigger");
    }

    [Fact]
    public void EveryStageIsCountedSeparately()
    {
        _tally.Heard();
        _tally.Recognised();
        _tally.Addressed();
        _tally.Answered();
        _tally.Echoed();

        _tally.Read().Should().Be(new VoiceCounts(1, 1, 1, 1, 1));
    }

    [Fact]
    public void CountingIsSafeFromEveryStreamAtOnce()
    {
        // Four people in a channel are four streams that finish talking at the same moment.
        Parallel.For(0, 1000, _ => _tally.Heard());

        _tally.Read().Heard.Should().Be(1000);
    }
}
