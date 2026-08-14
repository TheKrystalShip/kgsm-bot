using System.Collections.Concurrent;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Recognises every utterance and passes on the ones addressed to the bot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is recognised and almost all of it is thrown away.</b> There is no cheaper gate to
/// put first: finding the trigger means reading the words, and reading the words is the expensive
/// part. What that buys is that the trigger is a string an operator can change rather than a model
/// somebody has to train.
/// </para>
/// <para>
/// <b>What is not addressed to the bot leaves no trace.</b> A transcript that does not open with the
/// trigger is dropped without being logged at information level — the room is full of people talking
/// to each other, and a bot that writes down their conversation because it was in the channel is
/// doing something nobody agreed to. Only what somebody said *to* the bot is recorded.
/// </para>
/// <para>
/// <b>Saying the trigger alone is a real thing to do.</b> "Hey assistant" and then a pause is
/// somebody getting the bot's attention before they have decided on the words, which is how people
/// speak to assistants. It opens a short window in which that speaker's next utterance is taken as
/// the request without needing the trigger again.
/// </para>
/// </remarks>
public sealed class RecognisingUtteranceSink : IVoiceUtteranceSink
{
    private readonly ISpeechToText _speech;
    private readonly IVoiceCommandHandler _handler;
    private readonly ILogger<RecognisingUtteranceSink> _logger;
    private readonly WakeWordDetector _wake;
    private readonly TimeSpan _followUp;
    private readonly bool _logTranscripts;

    /// <summary>Speakers who said the trigger and have not yet said what they wanted.</summary>
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _waitingOn = new();

    public RecognisingUtteranceSink(
        ISpeechToText speech,
        IVoiceCommandHandler handler,
        IOptions<DiscordOptions> options,
        ILogger<RecognisingUtteranceSink> logger)
    {
        _speech = speech;
        _handler = handler;
        _logger = logger;

        VoiceOptions voice = options.Value.Voice;
        _followUp = TimeSpan.FromSeconds(Math.Max(1, voice.FollowUpSeconds));
        _logTranscripts = voice.LogTranscripts;

        string[] triggers = voice.Triggers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _wake = new WakeWordDetector(triggers);

        if (triggers.Length == 0)
            _logger.LogError(
                "Voice: no trigger phrase is configured — nothing said in a voice channel can reach the bot");
        else
            _logger.LogInformation(
                "Voice: listening for \"{Triggers}\"", string.Join("\", \"", triggers));

        if (_logTranscripts)
            _logger.LogWarning(
                "Voice: transcript logging is ON — everything said in a voice channel is written to "
                + "this host's log, including conversation not addressed to the bot");
    }

    public async ValueTask OnUtteranceAsync(VoiceUtterance utterance, CancellationToken ct = default)
    {
        if (!_speech.IsAvailable) return;

        string? transcript = await _speech.TranscribeAsync(utterance, ct);
        if (string.IsNullOrWhiteSpace(transcript)) return;

        // Deliberately the whole transcript and deliberately off by default: this is what an operator
        // tunes a trigger phrase against, and there is no way to show them how the recogniser heard
        // their phrase without showing them what it heard.
        if (_logTranscripts)
            _logger.LogInformation(
                "Voice: heard from {Speaker}: \"{Transcript}\"", utterance.SpeakerName, transcript);

        string? asked = _wake.Match(transcript);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (asked is null)
        {
            // Not addressed — unless this speaker said the trigger a moment ago and is only now
            // saying what they wanted.
            if (!TakeWindow(utterance.SpeakerId, now))
            {
                _logger.LogTrace("Voice: not addressed to the bot, dropped");
                return;
            }

            asked = transcript;
        }
        else if (asked.Length == 0)
        {
            // The trigger and nothing else. Hold the door open rather than dispatching an empty
            // request, which the assistant could only answer with a question.
            _waitingOn[utterance.SpeakerId] = now + _followUp;
            _logger.LogInformation(
                "Voice: {Speaker} said the trigger — listening for what they want", utterance.SpeakerName);
            return;
        }
        else
        {
            // A complete request in one breath. Any window this speaker had is spent by it: what
            // they came to say has now been said.
            _waitingOn.TryRemove(utterance.SpeakerId, out _);
        }

        _logger.LogInformation(
            "Voice: {Speaker} asked \"{Asked}\" ({Spoken:F1}s)",
            utterance.SpeakerName, asked, utterance.Duration.TotalSeconds);

        var command = new VoiceCommand(
            utterance.SpeakerId, utterance.SpeakerName, utterance.GuildId, utterance.ChannelId,
            asked, transcript, utterance.Duration);

        await _handler.HandleAsync(command, ct);
    }

    /// <summary>
    /// Whether this speaker is inside the window opened by a bare trigger, consuming it if so.
    /// </summary>
    /// <remarks>
    /// An expired window is removed on the way past rather than by a sweep. The only thing that can
    /// accumulate here is one timestamp per person who has ever addressed the bot in this process,
    /// and the read that would notice it is the one doing the removing.
    /// </remarks>
    private bool TakeWindow(ulong speakerId, DateTimeOffset now)
    {
        if (!_waitingOn.TryGetValue(speakerId, out DateTimeOffset expires)) return false;

        _waitingOn.TryRemove(speakerId, out _);
        return expires > now;
    }
}
