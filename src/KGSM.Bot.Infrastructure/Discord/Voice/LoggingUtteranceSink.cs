using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Reports that an utterance was captured, and drops it.
/// </summary>
/// <remarks>
/// <para>
/// What the voice surface does with speech is recognition's business, and there is no recogniser
/// here yet. This sink makes the capture half observable on its own — how many utterances a
/// conversation produces, how long they run, whether the silence gap is cutting people off — which
/// is what those durations have to be tuned against.
/// </para>
/// <para>
/// It logs the shape of an utterance and never its contents, which at this stage it could not do
/// anyway: audio is not text, and the log is not where either belongs.
/// </para>
/// </remarks>
public sealed class LoggingUtteranceSink(ILogger<LoggingUtteranceSink> logger) : IVoiceUtteranceSink
{
    public ValueTask OnUtteranceAsync(VoiceUtterance utterance, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Voice: heard {Duration:F1}s from {Speaker} ({Bytes} bytes of 16 kHz mono)",
            utterance.Duration.TotalSeconds, utterance.SpeakerName, utterance.Audio.Length);

        return ValueTask.CompletedTask;
    }
}
