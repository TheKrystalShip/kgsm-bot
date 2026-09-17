using System.Diagnostics;

using KGSM.Bot.Core.Interfaces;
using TheKrystalShip.Discord.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.Speech;

namespace KGSM.Bot.Infrastructure.Speech;

/// <summary>
/// Recognition, as the rest of the bot asks for it: an utterance in, words out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model is in another process.</b> What happens here is everything around the model that the
/// bot knows and the model does not — which names to expect, what a transcript that is really the
/// prompt coming back looks like, and what to count. The worker is handed audio and a list of names
/// and hands back a string; it has no idea what a server is.
/// </para>
/// <para>
/// <b>Reading the inventory lives on this side; writing the names down does not.</b> A recogniser that
/// has never been told a server is called <c>Ketchup</c> spells it "catch-up", which is a correct
/// reading of the sound and the wrong answer. <see cref="SpokenVocabulary"/> — the speech package's,
/// shared with every other surface that listens — composes the names and catches the echo they can
/// come back as; this refreshes them from the inventory as servers come and go, and the daemon
/// rebuilds its processor when the string it is sent changes.
/// </para>
/// </remarks>
internal sealed class LeafSpeechToText : ISpeechToText
{
    /// <summary>
    /// How often to look for servers having been installed or removed.
    /// </summary>
    /// <remarks>
    /// The inventory is cached, so asking is cheap — but it is asked on the path between somebody
    /// finishing a sentence and the answer starting. A server installed is not heard about for up to
    /// this long, which costs one misheard name.
    /// </remarks>
    private static readonly TimeSpan VocabularyInterval = TimeSpan.FromMinutes(2);

    private readonly HostSpeech _speech;
    private readonly IKgsmStateCache _inventory;
    private readonly IVoiceTally _tally;
    private readonly ILogger<LeafSpeechToText> _logger;
    private readonly string[] _triggers;
    private readonly bool _prime;

    private string _vocabulary = string.Empty;
    private DateTimeOffset _vocabularyCheckedAt = DateTimeOffset.MinValue;

    public LeafSpeechToText(
        HostSpeech speech,
        IOptions<DiscordOptions> options,
        IKgsmStateCache inventory,
        IVoiceTally tally,
        ILogger<LeafSpeechToText> logger)
    {
        _speech = speech;
        _inventory = inventory;
        _tally = tally;
        _logger = logger;

        VoiceOptions voice = options.Value.Voice;
        _prime = voice.PrimeWithServerNames;
        _triggers = voice.Triggers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool IsAvailable => _speech.Enabled && _speech.Installed;

    public async Task<string?> TranscribeAsync(VoiceUtterance utterance, CancellationToken ct = default) =>
        (await RecogniseAsync(utterance, ifIdle: false, ct)).Transcript;

    public Task<IdleReading> TranscribeIfIdleAsync(VoiceUtterance utterance, CancellationToken ct = default) =>
        RecogniseAsync(utterance, ifIdle: true, ct);

    private async Task<IdleReading> RecogniseAsync(
        VoiceUtterance utterance, bool ifIdle, CancellationToken ct)
    {
        if (!IsAvailable) return IdleReading.Of(null);

        string vocabulary = await PrimingAsync(ct);

        var timer = Stopwatch.StartNew();
        (SpeechProtocol.Outcome outcome, string text) =
            await _speech.Client.TranscribeAsync(utterance.Audio, vocabulary, ifIdle, ct);
        timer.Stop();

        if (outcome == SpeechProtocol.Outcome.Busy)
        {
            // Said out loud in the log because from inside a channel this is invisible: a busy room is
            // exactly when the recogniser is occupied, and the opening is read on a later offer, so
            // the only symptom is a tone that seems late. Counting these is how an operator tells
            // contention apart from a trigger that is not matching.
            _logger.LogDebug(
                "Voice: skipped reading {Speaker} early — the recogniser was busy", utterance.SpeakerName);

            return IdleReading.Busy;
        }

        if (outcome != SpeechProtocol.Outcome.Done) return IdleReading.Of(null);

        string transcript = SpokenTranscript.Clean(text);

        _logger.LogDebug(
            "Voice: recognised {Spoken:F1}s{Partial} from {Speaker} in {Elapsed}ms",
            utterance.Duration.TotalSeconds, utterance.Partial ? " so far" : string.Empty,
            utterance.SpeakerName, timer.ElapsedMilliseconds);

        if (SpokenVocabulary.IsEchoOf(transcript, vocabulary))
        {
            // Not counted for a partial: the same audio comes back complete a moment later and would be
            // counted again, turning one misfire into two in the numbers an operator reads to find out
            // whether priming is misbehaving.
            if (!utterance.Partial) _tally.Echoed();

            // Whisper continuing the context it was primed with rather than admitting it heard nothing.
            // Reported at debug and as a count, because a run of these is how an operator finds out the
            // priming is misfiring on a quiet channel.
            _logger.LogDebug(
                "Voice: discarded a transcript from {Speaker} that was the primed vocabulary coming back",
                utterance.SpeakerName);

            return IdleReading.Of(null);
        }

        return IdleReading.Of(transcript.Length == 0 ? null : transcript);
    }

    /// <summary>
    /// The names to expect, kept in step with what is installed.
    /// </summary>
    /// <remarks>
    /// An inventory that cannot be read leaves the previous names standing rather than clearing them.
    /// The names did not stop being the names because the engine could not be asked, and a recogniser
    /// that forgets them mid-outage starts mishearing every server at the moment somebody most needs to
    /// ask about one.
    /// </remarks>
    private async Task<string> PrimingAsync(CancellationToken ct)
    {
        if (!_prime) return string.Empty;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _vocabularyCheckedAt < VocabularyInterval) return _vocabulary;
        _vocabularyCheckedAt = now;

        try
        {
            IReadOnlyDictionary<string, Instance> instances = await _inventory.GetInstancesAsync(ct);
            IReadOnlyDictionary<string, Blueprint> blueprints = await _inventory.GetBlueprintsAsync(ct);
            string composed = SpokenVocabulary.Compose(_triggers, instances.Keys, blueprints.Keys);

            if (composed != _vocabulary)
            {
                _vocabulary = composed;
                _logger.LogInformation(
                    "Voice: priming the recogniser with {Characters} characters of this host's names",
                    composed.Length);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: could not read the inventory to prime the recogniser");
        }

        return _vocabulary;
    }
}
