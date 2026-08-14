using FluentAssertions;

using KGSM.Bot.Infrastructure.Speech;

using Xunit;

namespace KGSM.Bot.Core.Tests.Speech;

/// <summary>
/// The bot and the speech worker are two processes that only ever hear each other through this, so a
/// field written on one side and read at the wrong offset on the other is not a compile error — it is
/// audio that arrives as gibberish, or a length prefix that makes the reader allocate whatever the
/// noise happened to say.
/// </summary>
public class SpeechProtocolTests
{
    [Fact]
    public async Task AFrameComesBackWithItsKindAndItsBytes()
    {
        var pipe = new MemoryStream();
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Ready, [1, 2, 3]);
        pipe.Position = 0;

        (SpeechProtocol.Kind Kind, byte[] Payload)? frame = await SpeechProtocol.ReadAsync(pipe);

        frame.Should().NotBeNull();
        frame!.Value.Kind.Should().Be(SpeechProtocol.Kind.Ready);
        frame.Value.Payload.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task FramesQueuedTogetherAreReadOneAtATime()
    {
        // The real stream carries several: an utterance finishing while an answer is being synthesised
        // puts two messages in flight, and a reader that took the whole buffer as one would lose both.
        var pipe = new MemoryStream();
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Transcribe, [7]);
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Synthesize, [8, 9]);
        pipe.Position = 0;

        (await SpeechProtocol.ReadAsync(pipe))!.Value.Payload.Should().Equal(7);
        (await SpeechProtocol.ReadAsync(pipe))!.Value.Payload.Should().Equal(8, 9);
    }

    [Fact]
    public async Task AClosedConnectionIsNoMessageRatherThanAFailure()
    {
        // How both ends find out the other has gone: the worker's read loop ends and it exits, the
        // bot's fails everything outstanding. Neither is an error worth throwing.
        (await SpeechProtocol.ReadAsync(new MemoryStream())).Should().BeNull();
    }

    [Fact]
    public async Task AHalfWrittenFrameIsNotReadAsAWholeOne()
    {
        var pipe = new MemoryStream();
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Synthesized, [1, 2, 3, 4, 5, 6]);

        // Cut short, as a connection dropping mid-message leaves it.
        var truncated = new MemoryStream(pipe.ToArray()[..8]);

        (await SpeechProtocol.ReadAsync(truncated)).Should().BeNull();
    }

    [Fact]
    public async Task AFrameClaimingAnAbsurdLengthIsRefused()
    {
        // A desynchronised stream reads noise as a length. Refusing beats allocating what it said.
        var pipe = new MemoryStream();
        pipe.Write(BitConverter.GetBytes(int.MaxValue));
        pipe.WriteByte((byte)SpeechProtocol.Kind.Transcribe);
        pipe.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await SpeechProtocol.ReadAsync(pipe));
    }

    [Fact]
    public void ReadyCarriesWhatTheWorkerTurnedOutToBeAbleToDo()
    {
        byte[] payload = SpeechProtocol.Ready(canHear: true, canSpeak: false, "ggml-small.en.bin on the GPU");

        (bool canHear, bool canSpeak, string detail) = SpeechProtocol.ReadReady(payload);

        canHear.Should().BeTrue();
        canSpeak.Should().BeFalse();
        detail.Should().Be("ggml-small.en.bin on the GPU");
    }

    [Fact]
    public void AnUtteranceSurvivesTheRoundTrip()
    {
        byte[] audio = Enumerable.Range(0, 32000).Select(i => (byte)(i % 251)).ToArray();

        byte[] payload = SpeechProtocol.Transcribe(42, ifIdle: true, "factorio, terraria", audio);
        (uint id, bool ifIdle, string vocabulary, byte[] back) = SpeechProtocol.ReadTranscribe(payload);

        id.Should().Be(42);
        ifIdle.Should().BeTrue();
        vocabulary.Should().Be("factorio, terraria");
        back.Should().Equal(audio);
    }

    [Fact]
    public void AnUtteranceWithNoNamesToExpectIsStillReadCorrectly()
    {
        // Priming is a setting, so the empty string is the ordinary case on plenty of hosts — and a
        // zero-length field is exactly where an offset bug hides.
        byte[] payload = SpeechProtocol.Transcribe(1, ifIdle: false, string.Empty, [9, 8, 7]);
        (_, bool ifIdle, string vocabulary, byte[] audio) = SpeechProtocol.ReadTranscribe(payload);

        ifIdle.Should().BeFalse();
        vocabulary.Should().BeEmpty();
        audio.Should().Equal(9, 8, 7);
    }

    [Theory]
    // The outcome travels as its number, so the cases are written as numbers: this is the one place
    // where reading the byte as the wrong member would be silent, and "busy" arriving as "done" is an
    // utterance nobody was ever told was skipped.
    [InlineData(0, "restart factorio")]
    [InlineData(1, "")]
    [InlineData(2, "")]
    [InlineData(3, "")]
    public void ATranscriptComesBackWithHowItWent(byte outcome, string text)
    {
        (uint id, SpeechProtocol.Outcome came, string said) = SpeechProtocol.ReadTranscribed(
            SpeechProtocol.Transcribed(7, (SpeechProtocol.Outcome)outcome, text));

        id.Should().Be(7);
        came.Should().Be((SpeechProtocol.Outcome)outcome);
        said.Should().Be(text);
    }

    [Fact]
    public void WhatWasSaidSurvivesBeingSaidInAnotherAlphabet()
    {
        // The transcript is whatever whisper produced and the request is whatever somebody typed;
        // neither is ASCII by contract, and a length counted in characters would truncate both.
        const string said = "перезапусти factorio — сейчас";

        SpeechProtocol.ReadTranscribed(SpeechProtocol.Transcribed(1, SpeechProtocol.Outcome.Done, said))
            .Text.Should().Be(said);

        (_, string voice, string text) =
            SpeechProtocol.ReadSynthesize(SpeechProtocol.Synthesize(1, "bf_emma", said));

        voice.Should().Be("bf_emma");
        text.Should().Be(said);
    }

    [Fact]
    public void AnAnswerToSayCarriesTheVoiceToSayItIn()
    {
        byte[] payload = SpeechProtocol.Synthesize(3, "af_heart", "One moment.");
        (uint id, string voice, string text) = SpeechProtocol.ReadSynthesize(payload);

        id.Should().Be(3);
        voice.Should().Be("af_heart");
        text.Should().Be("One moment.");
    }

    [Fact]
    public void SynthesisedAudioComesBackWhole()
    {
        // Ten seconds of 24 kHz mono, which is a long answer and the size this actually carries.
        byte[] audio = new byte[24000 * 2 * 10];
        Random.Shared.NextBytes(audio);

        (uint id, SpeechProtocol.Outcome outcome, byte[] back) =
            SpeechProtocol.ReadSynthesized(SpeechProtocol.Synthesized(11, SpeechProtocol.Outcome.Done, audio));

        id.Should().Be(11);
        outcome.Should().Be(SpeechProtocol.Outcome.Done);
        back.Should().Equal(audio);
    }

    [Fact]
    public void NothingSynthesisedIsAnEmptyPayloadRatherThanAMissingOne()
    {
        (_, SpeechProtocol.Outcome outcome, byte[] audio) = SpeechProtocol.ReadSynthesized(
            SpeechProtocol.Synthesized(2, SpeechProtocol.Outcome.Unavailable, []));

        outcome.Should().Be(SpeechProtocol.Outcome.Unavailable);
        audio.Should().BeEmpty();
    }
}
