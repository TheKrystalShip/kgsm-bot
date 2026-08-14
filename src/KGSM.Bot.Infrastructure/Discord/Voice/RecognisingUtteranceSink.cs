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
/// <b>A speaker can be owed more than one utterance, and there are two ways that happens.</b> Saying
/// the trigger alone — "hey assistant", then a pause while they decide on the words — is somebody
/// getting the bot's attention first, which is how people address assistants. And a request cut at
/// the utterance ceiling is a sentence with its end missing: measured turning "stop minecraft" into
/// "stop", which the assistant could only answer by asking which server. Both leave the same thing
/// pending — this speaker has not finished — so both hold what there is so far and complete it from
/// what they say next.
/// </para>
/// </remarks>
public sealed class RecognisingUtteranceSink : IVoiceUtteranceSink
{
    private readonly ISpeechToText _speech;
    private readonly VoiceCommandQueue _queue;
    private readonly IVoiceTally _tally;
    private readonly ILogger<RecognisingUtteranceSink> _logger;
    private readonly WakeWordDetector _wake;
    private readonly TimeSpan _followUp;
    private readonly bool _logTranscripts;

    /// <summary>Speakers who have addressed the bot and not yet finished saying what they want.</summary>
    private readonly ConcurrentDictionary<ulong, Pending> _pending = new();

    /// <param name="Text">What they have said so far — empty when they have only said the trigger.</param>
    /// <param name="Expires">When to stop waiting for the rest.</param>
    private sealed record Pending(string Text, DateTimeOffset Expires);

    public RecognisingUtteranceSink(
        ISpeechToText speech,
        VoiceCommandQueue queue,
        IVoiceTally tally,
        IOptions<DiscordOptions> options,
        ILogger<RecognisingUtteranceSink> logger)
    {
        _speech = speech;
        _queue = queue;
        _tally = tally;
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

        _tally.Heard();

        string? transcript = await _speech.TranscribeAsync(utterance, ct);
        if (string.IsNullOrWhiteSpace(transcript)) return;

        _tally.Recognised();

        // Deliberately the whole transcript and deliberately off by default: this is what an operator
        // tunes a trigger phrase against, and there is no way to show them how the recogniser heard
        // their phrase without showing them what it heard.
        if (_logTranscripts)
            _logger.LogInformation(
                "Voice: heard from {Speaker}: \"{Transcript}\"", utterance.SpeakerName, transcript);

        string? asked = _wake.Match(transcript);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Pending? owed = Take(utterance.SpeakerId, now);

        if (asked is null)
        {
            // Not addressed to the bot — unless this speaker is mid-request, in which case this is
            // the rest of it and needs no second trigger.
            if (owed is null)
            {
                _logger.LogTrace("Voice: not addressed to the bot, dropped");
                return;
            }

            asked = Join(owed.Text, transcript);
        }
        else if (owed is not null && owed.Text.Length > 0)
        {
            // They were finishing a cut-off request and said the trigger again inside it. The
            // earlier half is theirs too, so it is kept rather than dropped.
            asked = Join(owed.Text, asked);
        }

        // Everything from here was said to the bot, whether it opened with the trigger or is the rest
        // of something that did.
        _tally.Addressed();

        if (utterance.Truncated)
        {
            // The ceiling cut them off mid-sentence: what they say next is the end of this request,
            // not a new one. Dispatching now would put half a sentence to the assistant.
            _pending[utterance.SpeakerId] = new Pending(asked, now + _followUp);
            _logger.LogInformation(
                "Voice: {Speaker} is still talking — holding \"{Partial}\" for the rest",
                utterance.SpeakerName, asked);
            return;
        }

        if (asked.Length == 0)
        {
            // The trigger and nothing else. Hold the door open rather than dispatching an empty
            // request, which the assistant could only answer with a question.
            _pending[utterance.SpeakerId] = new Pending(string.Empty, now + _followUp);
            _logger.LogInformation(
                "Voice: {Speaker} said the trigger — listening for what they want", utterance.SpeakerName);
            return;
        }

        _logger.LogInformation(
            "Voice: {Speaker} asked \"{Asked}\" ({Spoken:F1}s)",
            utterance.SpeakerName, asked, utterance.Duration.TotalSeconds);

        var command = new VoiceCommand(
            utterance.SpeakerId, utterance.SpeakerName, utterance.GuildId, utterance.ChannelId,
            asked, transcript, utterance.Duration);

        _tally.Answered();

        // Handed over rather than answered here: answering takes seconds, and this runs on the loop
        // that closes other speakers' sentences.
        if (!_queue.Enqueue(command))
            _logger.LogWarning(
                "Voice: dropped {Speaker}'s request — more arrived than could be answered",
                utterance.SpeakerName);
    }

    /// <summary>
    /// What this speaker is owed, consuming it. Null when they are not mid-request or waited too long.
    /// </summary>
    /// <remarks>
    /// An expired entry is dropped on the way past rather than by a sweep. The most that can
    /// accumulate is one per person who has addressed the bot in this process, and the read that
    /// would notice it is the one doing the removing.
    /// </remarks>
    private Pending? Take(ulong speakerId, DateTimeOffset now)
    {
        if (!_pending.TryRemove(speakerId, out Pending? pending)) return null;
        return pending.Expires > now ? pending : null;
    }

    /// <summary>Joins two halves of one request without inventing punctuation between them.</summary>
    private static string Join(string first, string second) =>
        first.Length == 0 ? second
        : second.Length == 0 ? first
        : $"{first} {second}";
}
