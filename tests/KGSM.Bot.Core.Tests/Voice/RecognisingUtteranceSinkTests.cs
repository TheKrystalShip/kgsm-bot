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
            _speech, _queue, _tally, options, NullLogger<RecognisingUtteranceSink>.Instance);
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
}
