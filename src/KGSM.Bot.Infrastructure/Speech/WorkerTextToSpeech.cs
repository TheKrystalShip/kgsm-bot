using System.Collections.Concurrent;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Speech;

/// <summary>
/// Speaking, as the rest of the bot asks for it: an answer in, audio out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model is in another process; the choosing is here.</b> Which voice to speak in, which voices
/// this host has, and which phrases are worth keeping are all answered without asking the worker
/// anything — the voices are files on a disk this process can read, so the picker and the setting work
/// on a bot that has never started a worker at all.
/// </para>
/// <para>
/// <b>The worker returns 24 kHz mono</b>, as Kokoro produces it, and the conversion to the 48 kHz
/// stereo a Discord connection takes happens here. That is four times fewer bytes on the socket, and
/// the conversion is exact — see <see cref="PcmUpsampler"/>.
/// </para>
/// </remarks>
internal sealed class WorkerTextToSpeech : ITextToSpeech
{
    /// <summary>
    /// Short phrases already synthesised, kept as the audio they became.
    /// </summary>
    /// <remarks>
    /// The acknowledgements are said over and over, word for word, and they are the one thing here
    /// whose whole value is arriving immediately — synthesising "One moment." again every time is
    /// paying the latency it exists to hide. On this side of the socket rather than the worker's,
    /// because a cached phrase should not cost a round trip either. Bounded and only for short text: a
    /// cache of whole answers would hold megabytes of audio nobody will hear twice.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte[]> _said = new();

    private const int LongestWorthKeeping = 100;
    private const int MostToKeep = 24;

    private readonly SpeechWorker _worker;
    private readonly ILogger<WorkerTextToSpeech> _logger;

    /// <summary>
    /// The voice being spoken in. Mutable because it can be changed while the bot is running, and read
    /// on the synthesis path without a lock — a swap that lands between two sentences is exactly the
    /// behaviour wanted, and one that lands mid-sentence cannot happen: the name is read once, when the
    /// request is composed.
    /// </summary>
    private volatile string _voice;

    public WorkerTextToSpeech(
        SpeechWorker worker, IOptions<DiscordOptions> options, ILogger<WorkerTextToSpeech> logger)
    {
        _worker = worker;
        _logger = logger;
        _voice = options.Value.Voice.SpeechVoice;
    }

    public bool IsAvailable => _worker.CanSpeak;

    public string SpeakingAs => _worker.CanSpeak ? _voice : string.Empty;

    public IReadOnlyList<string> Voices => _worker.CanSpeak ? InstalledVoices.Offered() : [];

    public Result SpeakAs(string voice)
    {
        if (!_worker.CanSpeak)
            return Result.Failure("There's no speech synthesis on this host.");

        if (string.IsNullOrWhiteSpace(voice))
            return Result.Failure("Name a voice.");

        string? file = InstalledVoices.Find(voice);
        if (file is null)
            return Result.Failure($"I don't have a voice called \"{voice}\".");

        string named = Path.GetFileNameWithoutExtension(file);
        if (named.Equals(_voice, StringComparison.OrdinalIgnoreCase)) return Result.Success();

        _voice = named;

        // ⚠ Cleared with it. The cache is keyed by TEXT, so every phrase in it is audio in the voice
        // that has just been replaced — leaving it would have the bot answer in the new voice and go on
        // acknowledging in the old one, which reads as a half-applied change rather than as a cache.
        _said.Clear();

        _logger.LogInformation("Voice: now speaking as {Voice}", named);
        return Result.Success();
    }

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (!_worker.CanSpeak || string.IsNullOrWhiteSpace(text)) return null;

        // Read before anything is sent: a phrase already synthesised does not queue behind whatever is
        // being synthesised now, which is the difference between an acknowledgement that is immediate
        // and one that waits for the answer it is meant to precede.
        if (_said.TryGetValue(text, out byte[]? already)) return already;

        byte[]? mono24k = await _worker.SynthesizeAsync(text, _voice, ct);
        if (mono24k is null || mono24k.Length == 0) return null;

        byte[] stereo48k = PcmUpsampler.ToStereo48k(mono24k);

        // Kept only while there is room, and never evicted to make room: the phrases worth holding are
        // a fixed short list said constantly, so whatever fills this first is what it is for. A cache
        // that churns would be paying bookkeeping to hold answers nobody repeats.
        if (text.Length <= LongestWorthKeeping && _said.Count < MostToKeep)
            _said[text] = stereo48k;

        return stereo48k;
    }
}
