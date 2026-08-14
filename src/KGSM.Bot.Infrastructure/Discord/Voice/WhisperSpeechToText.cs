using System.Diagnostics;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Models;

using Whisper.net;
using Whisper.net.LibraryLoader;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Recognises speech with whisper, in this process.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-process, not a service.</b> Recognition sits between a person finishing a sentence and the
/// assistant starting work, so every hop added to it is added to the wait. There is nothing else to
/// share it with either — this is the only thing on the host that listens.
/// </para>
/// <para>
/// <b>The model is loaded once and the processor is serialised.</b> Loading costs a large fraction of
/// a second and the weights are hundreds of megabytes, so doing it per utterance would dominate the
/// measurement it is part of. A whisper processor is not safe to use from two threads, and four
/// people in a channel are four streams that can finish talking at the same moment, so the lock is
/// what makes concurrent speakers correct rather than occasionally garbled.
/// </para>
/// <para>
/// <b>Cost is per utterance, not per second of audio.</b> Whisper pads what it is given to a fixed
/// thirty-second window, so a two-second question costs about what a ten-second one does. Utterance
/// length is therefore not a latency knob, and batching several together would be a real saving that
/// nothing here can take, because the person is waiting for the first one.
/// </para>
/// <para>
/// <b>It is primed with this host's own names.</b> A recogniser that has never been told a server is
/// called <c>Ketchup</c> spells it "catch-up", which is a correct reading of the sound and the wrong
/// answer. <see cref="SpokenVocabulary"/> composes the prior context and it is refreshed from the
/// inventory as servers come and go — see there for why this is done at the recogniser rather than by
/// correcting names afterwards.
/// </para>
/// </remarks>
public sealed class WhisperSpeechToText : ISpeechToText, IDisposable
{
    /// <summary>
    /// How often to look for servers having been installed or removed.
    /// </summary>
    /// <remarks>
    /// The inventory is cached, so asking is cheap — but it is asked on the path between somebody
    /// finishing a sentence and the answer starting, and rebuilding the processor is not free. A
    /// server installed is not heard about for up to this long, which costs one misheard name.
    /// </remarks>
    private static readonly TimeSpan VocabularyInterval = TimeSpan.FromMinutes(2);

    private readonly ILogger<WhisperSpeechToText> _logger;
    private readonly IKgsmStateCache _inventory;
    private readonly IVoiceTally _tally;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly WhisperFactory? _factory;
    private readonly string[] _triggers;
    private readonly bool _prime;

    private WhisperProcessor? _processor;
    private string _vocabulary = string.Empty;
    private DateTimeOffset _vocabularyCheckedAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public WhisperSpeechToText(
        IOptions<DiscordOptions> options,
        IKgsmStateCache inventory,
        IVoiceTally tally,
        ILogger<WhisperSpeechToText> logger)
    {
        _logger = logger;
        _inventory = inventory;
        _tally = tally;
        VoiceOptions voice = options.Value.Voice;

        _prime = voice.PrimeWithServerNames;
        _triggers = voice.Triggers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!voice.Enabled) return;

        string model = voice.ModelPath;
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
        {
            // Named rather than thrown: a host with the voice surface on and no model is
            // misconfigured, not broken, and everything else the bot does is unaffected.
            _logger.LogError(
                "Voice: no speech model at '{Model}' — nothing said in a voice channel will be understood. "
                + "Download one (ggml-small.en.bin) and set Discord:Voice:ModelPath.", model);
            return;
        }

        // Ask for the GPU and accept the CPU. Whisper.net picks the first runtime that initialises,
        // and a host whose driver is mid-upgrade, or which has no NVIDIA card at all, gets a slower
        // recogniser rather than no voice surface. Which one it settled on is logged below, because
        // the difference is a factor of forty and is otherwise invisible.
        RuntimeOptions.RuntimeLibraryOrder = voice.UseGpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];

        try
        {
            var timer = Stopwatch.StartNew();
            _factory = WhisperFactory.FromPath(model);
            _processor = Build(string.Empty);
            timer.Stop();

            _logger.LogInformation(
                "Voice: speech recognition ready — {Model}, {Runtime} preferred, loaded in {Elapsed}ms",
                Path.GetFileName(model), voice.UseGpu ? "GPU" : "CPU", timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice: could not load the speech model at '{Model}'", model);
            _factory = null;
            _processor = null;
        }
    }

    public bool IsAvailable => _processor is not null;

    public async Task<string?> TranscribeAsync(VoiceUtterance utterance, CancellationToken ct = default)
    {
        if (_processor is null) return null;

        await _one.WaitAsync(ct);
        try
        {
            await PrimeAsync(ct);
            if (_processor is null) return null;

            var timer = Stopwatch.StartNew();
            using var wav = WavStream(utterance.Audio);

            var text = new System.Text.StringBuilder();
            await foreach (SegmentData segment in _processor.ProcessAsync(wav, ct))
                text.Append(segment.Text);

            timer.Stop();
            string transcript = Spoken(text.ToString());

            _logger.LogDebug(
                "Voice: recognised {Spoken:F1}s from {Speaker} in {Elapsed}ms",
                utterance.Duration.TotalSeconds, utterance.SpeakerName, timer.ElapsedMilliseconds);

            if (SpokenVocabulary.IsEchoOf(transcript, _vocabulary))
            {
                _tally.Echoed();
                // Whisper continuing the context it was primed with rather than admitting it heard
                // nothing. Reported at debug and as a count, because a run of these is how an operator
                // finds out the priming is misfiring on a quiet channel.
                _logger.LogDebug(
                    "Voice: discarded a transcript from {Speaker} that was the primed vocabulary coming back",
                    utterance.SpeakerName);
                return null;
            }

            return transcript.Length == 0 ? null : transcript;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // One utterance failing to recognise is not the voice connection failing. The person is
            // still talking and the next thing they say may well come through.
            _logger.LogWarning(ex, "Voice: could not recognise an utterance from {Speaker}", utterance.SpeakerName);
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Builds a processor conditioned on <paramref name="vocabulary"/>.</summary>
    private WhisperProcessor Build(string vocabulary)
    {
        WhisperProcessorBuilder builder = _factory!.CreateBuilder().WithLanguage("en");
        return (vocabulary.Length == 0 ? builder : builder.WithPrompt(vocabulary)).Build();
    }

    /// <summary>
    /// Keeps the prior context in step with what is installed, rebuilding the processor when it moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called with the lock held and on the recognition path, so it does as little as it can: the
    /// inventory is only consulted every couple of minutes, and the processor is only rebuilt when the
    /// composed text actually differs — installing a server changes it, and the twenty checks that
    /// find nothing do not.
    /// </para>
    /// <para>
    /// An inventory that cannot be read leaves the previous context standing rather than clearing it.
    /// The names did not stop being the names because the engine could not be asked, and a recogniser
    /// that forgets them mid-outage starts mishearing every server at the moment somebody most needs
    /// to ask about one.
    /// </para>
    /// </remarks>
    private async Task PrimeAsync(CancellationToken ct)
    {
        if (!_prime || _factory is null) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _vocabularyCheckedAt < VocabularyInterval) return;
        _vocabularyCheckedAt = now;

        string composed;
        try
        {
            IReadOnlyDictionary<string, Instance> instances = await _inventory.GetInstancesAsync(ct);
            IReadOnlyDictionary<string, Blueprint> blueprints = await _inventory.GetBlueprintsAsync(ct);
            composed = SpokenVocabulary.Compose(_triggers, instances.Keys, blueprints.Keys);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: could not read the inventory to prime the recogniser");
            return;
        }

        if (composed == _vocabulary) return;

        try
        {
            WhisperProcessor rebuilt = Build(composed);
            _processor?.Dispose();
            _processor = rebuilt;
            _vocabulary = composed;

            _logger.LogInformation(
                "Voice: recogniser primed with {Characters} characters of this host's names", composed.Length);
        }
        catch (Exception ex)
        {
            // The processor that was working is still in place, so this costs the priming and not the
            // ability to recognise anything.
            _logger.LogWarning(ex, "Voice: could not prime the recogniser — carrying on without it");
        }
    }

    /// <summary>
    /// Strips whisper's annotations for sound that is not speech, leaving what was actually said.
    /// </summary>
    /// <remarks>
    /// Whisper describes non-speech rather than returning nothing for it: a breath, a keyboard or a
    /// held microphone comes back as <c>[BLANK_AUDIO]</c>, <c>[MUSIC]</c> or <c>(wind blowing)</c>.
    /// Those are notes about the audio, not words, and a caller that cannot tell the difference will
    /// hand <c>[BLANK_AUDIO]</c> to the assistant as a request. Everything bracketed goes, and what
    /// is left over is speech — which also means a whole utterance of nothing at all comes back
    /// empty, as it should.
    /// </remarks>
    internal static string Spoken(string raw)
    {
        var builder = new System.Text.StringBuilder(raw.Length);
        int square = 0, round = 0;

        foreach (char c in raw)
        {
            switch (c)
            {
                case '[': square++; continue;
                case ']': if (square > 0) square--; continue;
                case '(': round++; continue;
                case ')': if (round > 0) round--; continue;
            }

            if (square == 0 && round == 0) builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Wraps raw PCM in the WAV header whisper expects.
    /// </summary>
    /// <remarks>
    /// The audio is already 16 kHz mono signed 16-bit, which is whisper's native input, so this adds
    /// a 44-byte header and copies nothing else — there is no conversion here and there must not be
    /// one, because a resample at this point would be the second one applied to the same audio.
    /// </remarks>
    private static MemoryStream WavStream(byte[] pcm)
    {
        const int SampleRate = 16000;
        const short Channels = 1;
        const short BitsPerSample = 16;

        var stream = new MemoryStream(44 + pcm.Length);
        var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                                              // PCM header length
        writer.Write((short)1);                                        // uncompressed
        writer.Write(Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * BitsPerSample / 8);       // byte rate
        writer.Write((short)(Channels * BitsPerSample / 8));           // block align
        writer.Write(BitsPerSample);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);

        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _processor?.Dispose();
        _factory?.Dispose();
        _one.Dispose();
    }
}
