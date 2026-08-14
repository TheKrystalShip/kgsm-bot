using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Speech;

namespace KGSM.Bot.Infrastructure.Speech;

/// <summary>
/// This host's speech engine, as the bot reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The models are not in this process and are not this process's to manage.</b> They live in the
/// kgsm-speech leaf — one daemon per host, serving every surface that listens or speaks — because
/// 1.6GB of models and the CUDA runtime behind them cannot be given back by anything short of a
/// process ending. The bot connects to a socket; systemd starts the daemon on that connection and
/// stops it when nobody has needed it for a while.
/// </para>
/// <para>
/// <b>A host without the leaf is the ordinary case.</b> Everything here answers with an absence
/// rather than a failure, and the bot's voice surface degrades exactly as it does on a host with no
/// model files: it joins, it listens to nothing, and it answers in the channel's chat.
/// </para>
/// </remarks>
internal sealed class HostSpeech : ISpeechEngine, IDisposable
{
    private readonly SpeechClient _client;

    public HostSpeech(IOptions<DiscordOptions> options, ILogger<HostSpeech> logger)
    {
        VoiceOptions voice = options.Value.Voice;

        _client = new SpeechClient(
            string.IsNullOrWhiteSpace(voice.SpeechSocket) ? null : voice.SpeechSocket, logger);

        Enabled = voice.Enabled;
        Speaks = voice.Enabled && voice.Speak;
    }

    /// <summary>The client every speech call on this host goes through.</summary>
    public SpeechClient Client => _client;

    /// <summary>Whether this host has a speech daemon at all — asked without starting one.</summary>
    public bool Installed => _client.IsProvisioned;

    /// <summary>Whether the voice surface is switched on for this bot.</summary>
    public bool Enabled { get; }

    /// <summary>Whether this bot says its answers out loud as well as posting them.</summary>
    public bool Speaks { get; }

    public void Wake()
    {
        if (Enabled) _client.Wake();
    }

    public void Dispose() => _client.Dispose();
}
