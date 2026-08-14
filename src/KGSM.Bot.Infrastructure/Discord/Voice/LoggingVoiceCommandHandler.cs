using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Reports that the bot was asked something out loud, and does nothing about it.
/// </summary>
/// <remarks>
/// Hearing a request and acting on one are separate jobs, and only the second needs to know the
/// assistant exists. This makes the first observable on its own — whether the trigger is being
/// recognised, what the recogniser makes of a room with several people in it, and how long the whole
/// path takes — which are the questions worth answering before anything can act on the answer.
/// </remarks>
public sealed class LoggingVoiceCommandHandler(ILogger<LoggingVoiceCommandHandler> logger)
    : IVoiceCommandHandler
{
    public ValueTask HandleAsync(VoiceCommand command, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Voice command from {Speaker}: \"{Text}\" (heard: \"{Transcript}\")",
            command.SpeakerName, command.Text, command.Transcript);

        return ValueTask.CompletedTask;
    }
}
