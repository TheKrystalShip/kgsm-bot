using System.Diagnostics;
using System.Runtime.InteropServices;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using KokoroSharp;
using KokoroSharp.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Speaks with Kokoro, in this process and on the card.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the card, beside recognition and the language model.</b> Measured here, a short reply is
/// around 40ms on the GPU against 355ms on the processor, for roughly 700MB of video memory — which
/// the card has spare alongside gemma4 and whisper. The path needs cuDNN, a heavier dependency than
/// the cuBLAS whisper uses, so a host without it falls back rather than refusing.
/// </para>
/// <para>
/// <b>Cost scales with how much is said</b>, which is the opposite of recognition and worth knowing:
/// recognition pays a fixed price per utterance whatever its length, and this pays per character. A
/// reply that answers the question and stops is not merely nicer to listen to — a paragraph is
/// twelve seconds of talking nobody can skim.
/// </para>
/// <para>
/// Kokoro produces 24 kHz mono; Discord takes 48 kHz stereo. The conversion is exact and lives in
/// <see cref="PcmUpsampler"/>, so what this returns is ready to be written to a voice connection.
/// </para>
/// </remarks>
public sealed class KokoroTextToSpeech : ITextToSpeech, IDisposable
{
    /// <summary>
    /// Teaches the ONNX Runtime's P/Invokes where their library actually is on this host.
    /// </summary>
    /// <remarks>
    /// ONNX imports the literal name <c>onnxruntime.dll</c> on every platform, and .NET's default
    /// probing appends rather than replaces: it tries <c>onnxruntime.dll.so</c> and
    /// <c>libonnxruntime.dll.so</c> and never <c>libonnxruntime.so</c>, which is what is actually
    /// shipped. An ordinary publish papers over that with the RID asset mapping out of deps.json;
    /// a single-file one does not, and the symptom is a bot that reports having no synthesiser while
    /// the library sits beside it.
    /// </remarks>
    static KokoroTextToSpeech()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(Microsoft.ML.OnnxRuntime.SessionOptions).Assembly,
            (name, _, _) =>
            {
                if (!name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                string library = $"lib{Path.GetFileNameWithoutExtension(name)}.so";
                string beside = Path.Combine(AppContext.BaseDirectory, library);

                // Beside the binary first, then however the host resolves it — a packaged install
                // may well have it on the loader path instead.
                if (NativeLibrary.TryLoad(beside, out IntPtr handle)) return handle;
                return NativeLibrary.TryLoad(library, out handle) ? handle : IntPtr.Zero;
            });
    }

    /// <summary>
    /// Short phrases already synthesised, kept as the audio they became.
    /// </summary>
    /// <remarks>
    /// The acknowledgements are said over and over, word for word, and they are the one thing here
    /// whose whole value is arriving immediately — synthesising "One moment." again every time is
    /// paying the latency it exists to hide. Bounded and only for short text, because a cache of
    /// whole answers would hold megabytes of audio nobody will hear twice.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _said = new();

    private const int LongestWorthKeeping = 100;
    private const int MostToKeep = 24;

    private readonly ILogger<KokoroTextToSpeech> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly KokoroWavSynthesizer? _synth;

    /// <summary>
    /// The voice in use. Mutable because it can be changed while the bot is running, and read on the
    /// synthesis path without a lock — a swap that lands between two sentences is exactly the
    /// behaviour wanted, and one that lands mid-sentence cannot happen: the voice is read once, when
    /// the call is made.
    /// </summary>
    private volatile KokoroVoice? _voice;
    private bool _disposed;

    public KokoroTextToSpeech(IOptions<DiscordOptions> options, ILogger<KokoroTextToSpeech> logger)
    {
        _logger = logger;
        VoiceOptions voice = options.Value.Voice;

        if (!voice.Enabled || !voice.Speak) return;

        string model = voice.SpeechModelPath;
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
        {
            // Named rather than thrown: a host that cannot speak still hears, still answers in the
            // channel's chat, and still runs every other surface.
            _logger.LogError(
                "Voice: no speech synthesis model at '{Model}' — the bot will answer in text and not out loud. "
                + "Run deploy/setup.sh to fetch it, or set Discord:Voice:SpeechModelPath.", model);
            return;
        }

        try
        {
            var timer = Stopwatch.StartNew();
            (_synth, string runtime) = Load(model, voice.SpeakUseGpu);
            _voice = KokoroVoiceManager.GetVoice(voice.SpeechVoice);
            timer.Stop();

            _logger.LogInformation(
                "Voice: speech synthesis ready — {Voice} on the {Runtime}, loaded in {Elapsed}ms",
                voice.SpeechVoice, runtime, timer.ElapsedMilliseconds);

            Warm();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Voice: could not load speech synthesis ({Model}, voice {Voice}) — answers stay in text",
                model, voice.SpeechVoice);
            _synth = null;
            _voice = null;
        }
    }

    /// <summary>
    /// Synthesises one throwaway phrase so the first real answer does not pay for the last of the
    /// loading.
    /// </summary>
    /// <remarks>
    /// Measured in a live channel: the first answer of a session took 1441ms to synthesise and every
    /// one after it took 340-600ms. Loading the model is not the whole cost of the first call — the
    /// provider allocates and the graph is built on first inference — and that difference lands
    /// entirely on the first person to ask a question. Done in the background, because it is worth
    /// nothing to make startup wait for it: a request arriving before this finishes simply queues
    /// behind it, which is exactly what it would have paid anyway.
    /// </remarks>
    private void Warm() => _ = Task.Run(async () =>
    {
        try
        {
            var timer = Stopwatch.StartNew();
            await SynthesizeAsync("Ready.");
            timer.Stop();
            _logger.LogDebug("Voice: synthesis warmed in {Elapsed}ms", timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Nothing depends on this: a failure here costs the first answer its head start and no
            // more, and the real call will report its own problem properly.
            _logger.LogDebug(ex, "Voice: could not warm the synthesiser");
        }
    });

    /// <summary>
    /// Opens the model on the card when asked, and on the processor when the card cannot be had.
    /// </summary>
    /// <remarks>
    /// The CUDA path needs cuDNN present, which is a heavier dependency than the cuBLAS recognition
    /// uses and is genuinely absent on plenty of hosts. Failing over rather than refusing is what
    /// keeps one binary correct on a node with a card and on one without — and the fallback is
    /// reported, because it is an eightfold difference and otherwise invisible.
    /// </remarks>
    private (KokoroWavSynthesizer, string) Load(string model, bool useGpu)
    {
        if (useGpu)
        {
            try
            {
                var cuda = Microsoft.ML.OnnxRuntime.SessionOptions.MakeSessionOptionWithCudaProvider(0);
                return (KokoroWavSynthesizer.LoadModel(model, cuda), "GPU");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Voice: speech synthesis could not use the GPU ({Reason}) — falling back to the "
                    + "processor, which is around eight times slower", ex.Message);
            }
        }

        return (KokoroWavSynthesizer.LoadModel(model), "CPU");
    }

    public bool IsAvailable => _synth is not null && _voice is not null;

    public string SpeakingAs => _voice?.Name ?? string.Empty;

    /// <summary>
    /// The English voices this host has, best-trained first within each accent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from what is loaded</b>, so this is what the host actually has rather than what a list
    /// says it should. Every voice is already in memory: they load together on first use, and each is
    /// a small array of features.
    /// </para>
    /// <para>
    /// <b>English only.</b> Kokoro's other languages sit in the same directory and are loaded with
    /// these, but they expect text in those languages — offering them would be twenty-odd ways to read
    /// an English answer badly. <see cref="SpeakAs"/> accepts any of them, because refusing a voice the
    /// host has would be this surface deciding something it has no business deciding.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Voices
    {
        get
        {
            if (_synth is null) return [];

            var order = SpeechVoices.Preferred
                .Select((name, at) => (name, at))
                .ToDictionary(x => x.name, x => x.at, StringComparer.OrdinalIgnoreCase);

            return KokoroVoiceManager.Voices
                .Select(v => v.Name)
                .Where(n => n.Length > 1 && n[0] is 'a' or 'b')
                // A voice the preference list has never heard of sorts last rather than vanishing:
                // this decides what is suggested first, never what may be used.
                .OrderBy(n => order.TryGetValue(n, out int at) ? at : int.MaxValue)
                .ThenBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public Result SpeakAs(string voice)
    {
        if (_synth is null)
            return Result.Failure("There's no speech synthesis on this host.");

        if (string.IsNullOrWhiteSpace(voice))
            return Result.Failure("Name a voice.");

        KokoroVoice? found = KokoroVoiceManager.Voices
            .FirstOrDefault(v => v.Name.Equals(voice.Trim(), StringComparison.OrdinalIgnoreCase));

        // Looked up rather than asked for: the library's own accessor throws on an unknown name, and a
        // person mistyping one is not an exceptional condition.
        if (found is null)
            return Result.Failure($"I don't have a voice called \"{voice}\".");

        if (ReferenceEquals(found, _voice)) return Result.Success();

        _voice = found;

        // ⚠ Cleared with it. The cache is keyed by TEXT, so every phrase in it is audio in the voice
        // that has just been replaced — leaving it would have the bot answer in the new voice and go
        // on acknowledging in the old one, which reads as a half-applied change rather than a cache.
        _said.Clear();

        _logger.LogInformation("Voice: now speaking as {Voice}", found.Name);
        return Result.Success();
    }

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        // Read once, so a swap can land between sentences and never inside one.
        KokoroVoice? voice = _voice;
        if (_synth is null || voice is null || string.IsNullOrWhiteSpace(text)) return null;

        // Read before the lock: a phrase already synthesised does not queue behind whatever is being
        // synthesised now, which is the difference between an acknowledgement that is immediate and
        // one that waits for the answer it is meant to precede.
        if (_said.TryGetValue(text, out byte[]? already)) return already;

        await _one.WaitAsync(ct);
        try
        {
            var timer = Stopwatch.StartNew();

            // Synthesis is a blocking ONNX call. Off the caller's thread, because the caller is a
            // Discord event handler and the gateway's heartbeat runs on that machinery.
            byte[] mono24k = await Task.Run(() => _synth.Synthesize(text, voice), ct);
            byte[] stereo48k = PcmUpsampler.ToStereo48k(mono24k);

            timer.Stop();
            _logger.LogDebug(
                "Voice: synthesised {Characters} characters into {Audio:F1}s of audio in {Elapsed}ms",
                text.Length, PcmUpsampler.DurationOfStereo48k(stereo48k.Length).TotalSeconds,
                timer.ElapsedMilliseconds);

            // Kept only while there is room, and never evicted to make room: the phrases worth
            // holding are a fixed short list said constantly, so whatever fills this first is what
            // it is for. A cache that churns would be paying bookkeeping to hold answers nobody
            // repeats.
            if (text.Length <= LongestWorthKeeping && _said.Count < MostToKeep)
                _said[text] = stereo48k;

            return stereo48k;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Failing to say an answer out loud is not failing to answer: the text is already in the
            // channel, and the voice connection stays up for the next question.
            _logger.LogWarning(ex, "Voice: could not synthesise a reply — it stands in text only");
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _synth?.Dispose();
        _one.Dispose();
    }
}
