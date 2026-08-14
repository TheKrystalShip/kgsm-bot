using System.Diagnostics;
using System.Runtime.InteropServices;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using KokoroSharp;
using KokoroSharp.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Speaks with Kokoro, in this process and on the processor.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the processor, while recognition is on the card.</b> Kokoro can run on a GPU — it is an
/// ONNX model and takes session options — but that path needs cuDNN, which is a different dependency
/// from the cuBLAS whisper uses and is not what makes the difference here anyway. One card already
/// holds the language model, and that model is where a spoken answer spends most of its time; a
/// short reply synthesises in about 350ms on the processor and contends with nothing.
/// </para>
/// <para>
/// <b>Cost scales with how much is said</b>, which is the opposite of recognition and worth knowing:
/// "yes, it is" is around 350ms and a paragraph is a second or more. A reply that answers the
/// question and stops is not merely nicer to listen to, it arrives sooner — which is why keeping
/// replies short buys more here than moving the work to the card would.
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

    private readonly ILogger<KokoroTextToSpeech> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly KokoroWavSynthesizer? _synth;
    private readonly KokoroVoice? _voice;
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

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (_synth is null || _voice is null || string.IsNullOrWhiteSpace(text)) return null;

        await _one.WaitAsync(ct);
        try
        {
            var timer = Stopwatch.StartNew();

            // Synthesis is a blocking ONNX call. Off the caller's thread, because the caller is a
            // Discord event handler and the gateway's heartbeat runs on that machinery.
            byte[] mono24k = await Task.Run(() => _synth.Synthesize(text, _voice), ct);
            byte[] stereo48k = PcmUpsampler.ToStereo48k(mono24k);

            timer.Stop();
            _logger.LogDebug(
                "Voice: synthesised {Characters} characters into {Audio:F1}s of audio in {Elapsed}ms",
                text.Length, PcmUpsampler.DurationOfStereo48k(stereo48k.Length).TotalSeconds,
                timer.ElapsedMilliseconds);

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
