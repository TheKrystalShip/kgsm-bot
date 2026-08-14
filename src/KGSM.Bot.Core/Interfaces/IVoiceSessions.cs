using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Voice;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// The bot's presence in voice channels: which one it is listening in, per Discord server.
/// </summary>
/// <remarks>
/// <para>
/// <b>One channel per guild.</b> A second join in the same guild moves the bot rather than holding
/// two connections — Discord allows a bot one voice connection per guild, and pretending otherwise
/// would produce a second session that silently replaces the first.
/// </para>
/// <para>
/// <b>Listening is never implicit.</b> Nothing joins on its own: the bot is in a voice channel
/// because somebody asked it to be, and it leaves when asked or when the channel empties. A bot that
/// rejoins by itself is a microphone in a room nobody invited it into.
/// </para>
/// </remarks>
public interface IVoiceSessions
{
    /// <summary>Whether this host has the voice surface switched on at all.</summary>
    bool IsEnabled { get; }

    /// <summary>Joins <paramref name="channelId"/> and starts listening.</summary>
    Task<Result<VoiceSession>> JoinAsync(ulong guildId, ulong channelId, CancellationToken ct = default);

    /// <summary>Leaves whatever channel the bot is in for this guild.</summary>
    Task<Result> LeaveAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>What the bot is doing in this guild's voice, or null when it is in no channel.</summary>
    VoiceSession? Describe(ulong guildId);

    /// <summary>
    /// Says <paramref name="pcm"/> — 48 kHz stereo signed 16-bit — in this guild's voice channel.
    /// </summary>
    /// <remarks>
    /// Goes through the session because the session owns the connection. Two answers arriving at once
    /// are played one after the other rather than mixed into each other, which is what talking over
    /// yourself would sound like.
    /// </remarks>
    Task<Result> SpeakAsync(ulong guildId, byte[] pcm, CancellationToken ct = default);
}

/// <summary>
/// A live voice connection, as a surface can report it.
/// </summary>
/// <param name="GuildId">The Discord server.</param>
/// <param name="ChannelId">The channel being listened to.</param>
/// <param name="ChannelName">That channel's name, for a message somebody reads.</param>
/// <param name="JoinedAt">When the bot joined.</param>
/// <param name="Speakers">How many people it currently has audio streams for.</param>
/// <param name="Utterances">How many utterances it has assembled since joining.</param>
public sealed record VoiceSession(
    ulong GuildId,
    ulong ChannelId,
    string ChannelName,
    DateTimeOffset JoinedAt,
    int Speakers,
    long Utterances);

/// <summary>
/// Where a finished utterance goes.
/// </summary>
/// <remarks>
/// The seam between capturing speech and understanding it. Capture knows nothing about recognition,
/// which is what lets the recogniser be replaced, run elsewhere, or be absent entirely without the
/// voice connection knowing.
/// </remarks>
public interface IVoiceUtteranceSink
{
    /// <summary>
    /// Takes one finished utterance.
    /// </summary>
    /// <remarks>
    /// Must not throw and must not block the capture path: it is called from the loop draining a live
    /// audio stream, and time spent here is time frames are not being read.
    /// </remarks>
    ValueTask OnUtteranceAsync(VoiceUtterance utterance, CancellationToken ct = default);
}
