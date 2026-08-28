using System.Collections.Concurrent;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Speech;

/// <summary>
/// Speaking, as the rest of the bot asks for it: an answer in, audio out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model is in the kgsm-speech leaf; what is here is the Discord end of it.</b> The engine
/// returns 24 kHz mono, as Kokoro produces it, and the conversion to the 48 kHz stereo a voice
/// connection takes happens on this side — four times fewer bytes on the socket, and the conversion
/// is exact (<see cref="PcmUpsampler"/>).
/// </para>
/// <para>
/// <b>No voice is ever named.</b> The engine speaks in the host's voice, which is the whole point of
/// it living out there: a person hears the same assistant here as they do anywhere else on this host,
/// and there is no second setting to keep in step.
/// </para>
/// </remarks>
internal sealed class LeafTextToSpeech : ITextToSpeech
{
    /// <summary>
    /// Short phrases already synthesised, kept as the audio they became.
    /// </summary>
    /// <remarks>
    /// The acknowledgements are said over and over, word for word, and they are the one thing here
    /// whose whole value is arriving immediately — synthesising "One moment." again every time is
    /// paying the latency it exists to hide. On this side of the socket, because a cached phrase
    /// should not cost a round trip either. Bounded and only for short text: a cache of whole answers
    /// would hold megabytes of audio nobody will hear twice.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte[]> _said = new();

    private const int LongestWorthKeeping = 100;
    private const int MostToKeep = 24;

    private readonly HostSpeech _speech;
    private readonly ILogger<LeafTextToSpeech> _logger;

    public LeafTextToSpeech(HostSpeech speech, ILogger<LeafTextToSpeech> logger)
    {
        _speech = speech;
        _logger = logger;
    }

    public bool IsAvailable => _speech.Speaks && _speech.Installed;

    public async Task<(string Speaking, IReadOnlyList<string> Voices)> VoicesAsync(
        CancellationToken ct = default)
    {
        if (!IsAvailable) return (string.Empty, []);

        return await _speech.Client.VoicesAsync(ct);
    }

    public async Task<Result> SpeakAsAsync(string voice, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return Result.Failure("There's no speech synthesis on this host.");

        if (string.IsNullOrWhiteSpace(voice))
            return Result.Failure("Name a voice.");

        (bool changed, string speaking) = await _speech.Client.SpeakAsAsync(voice, ct);

        if (!changed)
            return Result.Failure($"There's no voice called \"{voice}\" on this host.");

        // Cleared, because the cache is keyed by TEXT: every phrase in it is audio in the voice that
        // has just been replaced. Leaving it would have the bot answer in the new voice and go on
        // acknowledging in the old one, which reads as a half-applied change rather than as a cache.
        _said.Clear();

        _logger.LogInformation("Voice: this host now speaks as {Voice}", speaking);
        return Result.Success();
    }

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;

        // Read before anything is sent: a phrase already synthesised does not queue behind whatever is
        // being synthesised now — for this bot or for any other surface sharing the engine — which is
        // the difference between an acknowledgement that is immediate and one that waits for the
        // answer it is meant to precede.
        if (_said.TryGetValue(text, out byte[]? already)) return already;

        byte[]? mono24k = await _speech.Client.SynthesizeAsync(text, voice: null, ct);
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
