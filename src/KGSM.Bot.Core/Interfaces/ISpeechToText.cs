using KGSM.Bot.Core.Voice;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Turns captured speech into words.
/// </summary>
/// <remarks>
/// The seam between hearing and understanding. Capture knows nothing about how recognition is done
/// and recognition knows nothing about Discord, which is what lets the recogniser be swapped, moved
/// off this machine, or be missing entirely without the voice connection changing.
/// </remarks>
public interface ISpeechToText
{
    /// <summary>Whether a recogniser is actually available to ask.</summary>
    /// <remarks>
    /// A model file that is not on disk is the ordinary case on a host that has not been set up for
    /// voice, and it is a fact worth reporting once at startup rather than discovering per utterance.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Transcribes 16 kHz mono signed 16-bit PCM.
    /// </summary>
    /// <remarks>
    /// Returns null when nothing was recognised, which is a normal outcome — a door closing and a
    /// cough both reach here as audio, and neither is words. Empty text and null mean the same thing
    /// to a caller and the implementation is free to return either.
    /// </remarks>
    Task<string?> TranscribeAsync(VoiceUtterance utterance, CancellationToken ct = default);
}

/// <summary>
/// Something somebody asked the bot to do, out loud.
/// </summary>
/// <param name="SpeakerId">The Discord account that said it.</param>
/// <param name="SpeakerName">That account's display name.</param>
/// <param name="GuildId">The Discord server it was said in.</param>
/// <param name="ChannelId">The voice channel it was said in.</param>
/// <param name="Text">
/// What was asked, with the trigger removed. Empty when somebody said the trigger and nothing else.
/// </param>
/// <param name="Transcript">The whole thing as recognised, trigger included, for the log.</param>
/// <param name="Spoken">How long the audio ran — the floor under any latency measured from it.</param>
public sealed record VoiceCommand(
    ulong SpeakerId,
    string SpeakerName,
    ulong GuildId,
    ulong ChannelId,
    string Text,
    string Transcript,
    TimeSpan Spoken,
    VoiceWaiting? Answering = null,
    bool Triggered = true);

/// <summary>
/// What to do about something the bot was asked out loud.
/// </summary>
/// <remarks>
/// Separate from the sink that produced it: recognising that somebody addressed the bot and deciding
/// what that means are different jobs, and only the second one needs to know the assistant exists.
/// </remarks>
public interface IVoiceCommandHandler
{
    /// <summary>Handles one spoken command. Must not throw.</summary>
    ValueTask HandleAsync(VoiceCommand command, CancellationToken ct = default);
}
