using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord.Voice;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// What reaches the assistant, and what does not. A speaker can be owed more than one utterance —
/// they said the trigger and paused, or the ceiling cut them off mid-sentence — and both were
/// measured producing a request the assistant could not act on.
/// </summary>
public class RecognisingUtteranceSinkTests
{
    private readonly ISpeechToText _speech = Substitute.For<ISpeechToText>();
    private readonly VoiceCommandQueue _queue = new();

    /// <summary>The real one, so the counts a surface reports are the ones this produces.</summary>
    private readonly VoiceTally _tally = new();

    /// <summary>Who the bot is waiting to hear back from — empty unless a test opens a window.</summary>
    private readonly VoiceAttention _attention = new();

    private readonly IVoiceChimes _chimes = Substitute.For<IVoiceChimes>();

    private readonly List<VoiceCommand> _taken = [];

    /// <summary>
    /// What the sink handed over — drained from the queue rather than intercepted, and accumulated,
    /// because a queue read twice is a queue that looks empty the second time.
    /// </summary>
    private List<VoiceCommand> _dispatched
    {
        get
        {
            while (_queue.Reader.TryRead(out VoiceCommand? command)) _taken.Add(command);
            return _taken;
        }
    }

    public RecognisingUtteranceSinkTests() => _speech.IsAvailable.Returns(true);

    private RecognisingUtteranceSink Sink(int followUpSeconds = 10)
    {
        var options = Options.Create(new DiscordOptions
        {
            Voice = new VoiceOptions { Triggers = "hey assistant", FollowUpSeconds = followUpSeconds },
        });

        return new RecognisingUtteranceSink(
            _speech, _queue, _tally, _attention, _chimes, options,
            NullLogger<RecognisingUtteranceSink>.Instance);
    }

    /// <summary>Feeds one utterance, with the transcript the recogniser would have produced.</summary>
    private async Task SayAsync(
        RecognisingUtteranceSink sink, string transcript, bool truncated = false, ulong speaker = 1)
    {
        var utterance = new VoiceUtterance(
            speaker, $"speaker{speaker}", GuildId: 7, ChannelId: 9,
            Audio: [], Duration: TimeSpan.FromSeconds(2), StartedAt: DateTimeOffset.UtcNow,
            Truncated: truncated);

        _speech.TranscribeAsync(utterance, Arg.Any<CancellationToken>()).Returns(transcript);
        await sink.OnUtteranceAsync(utterance);
    }

    [Fact]
    public async Task ARequestSaidInOneBreathIsPassedOn()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "Hey assistant, stop minecraft");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("stop minecraft");
        _dispatched[0].Transcript.Should().Be("Hey assistant, stop minecraft");
    }

    [Fact]
    public async Task ConversationNotAddressedToTheBotIsNotPassedOn()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "did you see the base I built");

        _dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task ARequestCutAtTheCeilingWaitsForTheRestOfIt()
    {
        // The measured failure: the ceiling landed after "stop", "minecraft" went into the next
        // utterance, and the assistant was asked to stop nothing in particular.
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "so anyway hey assistant, stop", truncated: true);
        _dispatched.Should().BeEmpty("half a request is not a request");

        await SayAsync(sink, "minecraft please");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("stop minecraft please");
    }

    [Fact]
    public async Task ARequestCutTwiceIsStillAssembled()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "hey assistant, stop", truncated: true);
        await SayAsync(sink, "the minecraft", truncated: true);
        await SayAsync(sink, "server");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("stop the minecraft server");
    }

    [Fact]
    public async Task TheTriggerAloneWaitsForTheRequest()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "Hey assistant");
        _dispatched.Should().BeEmpty();

        await SayAsync(sink, "restart factorio");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("restart factorio");
    }

    [Fact]
    public async Task SomebodyWhoSaysTheTriggerAgainMidRequestKeepsWhatTheyAlreadySaid()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "hey assistant, stop", truncated: true);
        await SayAsync(sink, "hey assistant, minecraft");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("stop minecraft");
    }

    [Fact]
    public async Task WaitingDoesNotGoOnForever()
    {
        // Without an expiry, something said minutes later — to a person, not to the bot — would be
        // glued onto a request nobody finished and acted on.
        RecognisingUtteranceSink sink = Sink(followUpSeconds: 1);

        await SayAsync(sink, "hey assistant, stop", truncated: true);
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await SayAsync(sink, "did you see the base I built");

        _dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task OnePersonsPendingRequestIsNotFinishedBySomebodyElse()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "hey assistant, stop", truncated: true, speaker: 1);
        await SayAsync(sink, "I'm going to get a coffee", speaker: 2);

        _dispatched.Should().BeEmpty("speaker 2 was talking to the room, not finishing speaker 1's sentence");

        await SayAsync(sink, "minecraft", speaker: 1);

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("stop minecraft");
        _dispatched[0].SpeakerId.Should().Be(1);
    }

    [Fact]
    public async Task NothingRecognisedIsNotARequest()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "");
        await SayAsync(sink, null!);

        _dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task WithNoRecogniserNothingIsPassedOn()
    {
        // A host with the voice surface on and no model still holds the connection; it simply
        // understands nothing, and must not put silence to the assistant.
        _speech.IsAvailable.Returns(false);
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "Hey assistant, stop minecraft");

        _dispatched.Should().BeEmpty();
        await _speech.DidNotReceive().TranscribeAsync(Arg.Any<VoiceUtterance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheRoomTheRequestWasSaidInTravelsWithIt()
    {
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "Hey assistant, stop minecraft");

        _dispatched[0].GuildId.Should().Be(7);
        _dispatched[0].ChannelId.Should().Be(9);
    }

    [Fact]
    public async Task SomebodyTheBotJustAskedSomethingAnswersWithoutTheTrigger()
    {
        // Measured in a real session: answering the assistant's own question took "hey assistant,
        // yes". The bot had spoken to that person a second earlier and still made them re-introduce
        // themselves.
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9, new VoiceWaiting(VoiceWaitingFor.Answer, DateTimeOffset.UtcNow.AddSeconds(20)));

        await SayAsync(sink, "yes please");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("yes please");
    }

    [Fact]
    public async Task OnlyThePersonWhoWasAskedAnswersWithoutTheTrigger()
    {
        // The room is full of other people. A window opened for one of them must not turn the rest
        // of the conversation into requests.
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9, new VoiceWaiting(VoiceWaitingFor.Answer, DateTimeOffset.UtcNow.AddSeconds(20)));

        await SayAsync(sink, "no I meant the other one", speaker: 2);

        _dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task OneUtteranceSpendsTheWindow()
    {
        // It must not become a microphone that is simply open: whatever they said next was the
        // answer, and the sentence after that is the room's again.
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9, new VoiceWaiting(VoiceWaitingFor.Answer, DateTimeOffset.UtcNow.AddSeconds(20)));

        await SayAsync(sink, "necesse");
        await SayAsync(sink, "anyway what were you saying");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("necesse");
    }

    [Fact]
    public async Task AnAnswerThatNeverCameDoesNotOpenTheDoorLater()
    {
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9, new VoiceWaiting(VoiceWaitingFor.Answer, DateTimeOffset.UtcNow.AddSeconds(-1)));

        await SayAsync(sink, "did you see the base I built");

        _dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnsweredQuestionIsCountedAsAddressedToTheBot()
    {
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9, new VoiceWaiting(VoiceWaitingFor.Answer, DateTimeOffset.UtcNow.AddSeconds(20)));

        await SayAsync(sink, "the second one");

        _tally.Read().Addressed.Should().Be(1);
        _tally.Read().Answered.Should().Be(1);
    }

    [Fact]
    public async Task SayingTheTriggerOutOfHabitStillAnswersTheQuestion()
    {
        // Somebody used to addressing the bot says "hey assistant, yes" while it is waiting on them.
        // Dropping the window there would send "yes" to the assistant as a fresh question and leave
        // the action they were approving unconfirmed.
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9,
            new VoiceWaiting(VoiceWaitingFor.Confirmation, DateTimeOffset.UtcNow.AddSeconds(20), ["abc123"]));

        await SayAsync(sink, "hey assistant, yes");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("yes");
        _dispatched[0].Answering!.For.Should().Be(VoiceWaitingFor.Confirmation);
        _dispatched[0].Answering!.Tokens.Should().ContainSingle().Which.Should().Be("abc123");
    }

    [Fact]
    public async Task TheTriggerAloneIntoAnOpenWindowIsNotSilentlyLost()
    {
        // The window is spent by this utterance either way, so an empty request would vanish along
        // with it. Held as what was said, to be read as an unclear answer and asked about again.
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9,
            new VoiceWaiting(VoiceWaitingFor.Confirmation, DateTimeOffset.UtcNow.AddSeconds(20), ["abc123"]));

        await SayAsync(sink, "hey assistant");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().NotBeEmpty();
        _dispatched[0].Answering!.For.Should().Be(VoiceWaitingFor.Confirmation);
    }

    [Fact]
    public async Task AnUntriggeredAnswerIsMarkedAsSuch()
    {
        RecognisingUtteranceSink sink = Sink();
        _attention.Expect(speakerId: 1, channelId: 9,
            new VoiceWaiting(VoiceWaitingFor.Confirmation, DateTimeOffset.UtcNow.AddSeconds(20), ["abc123"]));

        await SayAsync(sink, "go ahead");

        _dispatched[0].Triggered.Should().BeFalse("they answered rather than addressing the bot again");
    }

    [Fact]
    public async Task TheTriggerOnItsOwnIsAnsweredWithTheListeningTone()
    {
        // The one moment the bot is waiting and has otherwise said nothing about it. Silence here is
        // indistinguishable from a missed trigger, and what a person does about that is repeat it.
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "hey assistant");

        await _chimes.Received(1).PlayAsync(7, VoiceChime.Listening, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARequestSaidInOneBreathGetsNoListeningTone()
    {
        // Nothing is being waited for: the request is complete and on its way. The tone that belongs
        // here is the falling one, and it is played where the request is taken up rather than here.
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "Hey assistant, stop minecraft");

        await _chimes.DidNotReceive().PlayAsync(
            Arg.Any<ulong>(), Arg.Any<VoiceChime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BeingCutOffMidSentenceIsNotToned()
    {
        // Also a waiting state, and deliberately silent: they are still talking, and a tone played
        // over somebody mid-sentence interrupts the half of the request that has not been said yet.
        RecognisingUtteranceSink sink = Sink();

        await SayAsync(sink, "hey assistant, stop", truncated: true);

        await _chimes.DidNotReceive().PlayAsync(
            Arg.Any<ulong>(), Arg.Any<VoiceChime>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Feeds the opening of a sentence somebody is still in the middle of saying.</summary>
    private async Task StartSayingAsync(
        RecognisingUtteranceSink sink, string sofar, ulong speaker = 1)
    {
        var utterance = new VoiceUtterance(
            speaker, $"speaker{speaker}", GuildId: 7, ChannelId: 9,
            Audio: [], Duration: TimeSpan.FromMilliseconds(1500), StartedAt: DateTimeOffset.UtcNow,
            Truncated: false, Partial: true);

        _speech.TranscribeIfIdleAsync(utterance, Arg.Any<CancellationToken>()).Returns(sofar);
        await sink.OnUtteranceAsync(utterance);
    }

    [Fact]
    public async Task BeingAddressedIsNoticedBeforeTheSentenceIsFinished()
    {
        // The whole point: the tone that says "go ahead" arrives while somebody is still talking,
        // rather than after they have already said everything they wanted to.
        RecognisingUtteranceSink sink = Sink();

        await StartSayingAsync(sink, "Hey assistant, is min");

        await _chimes.Received(1).PlayAsync(7, VoiceChime.Listening, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOpeningNotAddressedToTheBotIsSilent()
    {
        RecognisingUtteranceSink sink = Sink();

        await StartSayingAsync(sink, "so then I told him that the");

        await _chimes.DidNotReceive().PlayAsync(
            Arg.Any<ulong>(), Arg.Any<VoiceChime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadingAheadDecidesNothing()
    {
        // ⚠ The property the whole mechanism rests on. Half a sentence must never reach the
        // assistant, be counted, or open a window — it is an instruction nobody has finished giving,
        // and the complete copy arrives a moment later.
        RecognisingUtteranceSink sink = Sink();

        await StartSayingAsync(sink, "Hey assistant, uninstall min");

        _dispatched.Should().BeEmpty();
        _tally.Read().Heard.Should().Be(0);
        _tally.Read().Addressed.Should().Be(0);
    }

    [Fact]
    public async Task ABusyRecogniserSkipsTheLookAheadRatherThanQueueing()
    {
        // Null is "nothing recognised" and "nothing attempted" alike, and the response to both is to
        // carry on: somebody's finished sentence is always worth more than a look at an unfinished one.
        RecognisingUtteranceSink sink = Sink();

        await StartSayingAsync(sink, null!);

        await _chimes.DidNotReceive().PlayAsync(
            Arg.Any<ulong>(), Arg.Any<VoiceChime>(), Arg.Any<CancellationToken>());
        _dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task TheWholeSentenceIsStillReadAfterItWasNoticedEarly()
    {
        // The look ahead is additive. What it must not do is consume the request.
        RecognisingUtteranceSink sink = Sink();

        await StartSayingAsync(sink, "Hey assistant, stop min");
        await SayAsync(sink, "Hey assistant, stop minecraft");

        _dispatched.Should().ContainSingle();
        _dispatched[0].Text.Should().Be("stop minecraft");
    }
}
