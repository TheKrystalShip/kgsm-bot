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
    /// <para>
    /// Goes through the session because the session owns the connection. Two answers arriving at once
    /// are played one after the other rather than mixed into each other, which is what talking over
    /// yourself would sound like.
    /// </para>
    /// <para>
    /// <paramref name="waitAtMost"/> caps how long this will queue behind whatever is already
    /// playing, and giving up is reported as a failure. It is for audio whose <em>meaning depends on
    /// when it arrives</em>: a tone saying "your turn" that comes out three seconds late describes a
    /// moment that has passed, and the person hears it against whatever is happening then instead.
    /// Null waits, which is what an answer wants — an answer is worth having whenever it lands.
    /// </para>
    /// </remarks>
    Task<Result> SpeakAsync(
        ulong guildId, byte[] pcm, TimeSpan? waitAtMost = null, CancellationToken ct = default);

    /// <summary>
    /// Opens a recital in this guild: one answer spoken in pieces as it is written. Null when the bot
    /// is in no voice channel here.
    /// </summary>
    /// <remarks>
    /// <b>Opening one makes it the current recital</b>, which abandons any that was still running —
    /// a guild has one voice connection and therefore one answer being spoken at a time.
    /// </remarks>
    IVoiceRecital? BeginRecital(ulong guildId);

    /// <summary>
    /// Drops the rest of whatever is being said out loud in this guild. Returns whether there was
    /// anything to drop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is dropped is the sound, never the answer.</b> The reply is already in the chat and
    /// the turn that produced it has finished — this ends a recital, and nothing is undone by ending
    /// it. That is what makes cutting the bot off safe to get wrong: the worst case is somebody
    /// reading the rest instead of hearing it.
    /// </para>
    /// <para>
    /// <b>The whole of an answer goes, not the sentence in the air.</b> An answer spoken as it is
    /// written is a queue of pieces, and stopping only the one playing leaves the bot pausing and
    /// then talking over the person who cut in — which is worse than not letting them cut in at all.
    /// So the current recital is abandoned as well, and every piece still owed to it is refused.
    /// </para>
    /// <para>
    /// <b>Answered rather than requested, so the caller can tell.</b> A false return means the bot
    /// was neither talking nor part-way through a recital, which is a different thing from having
    /// been stopped, and only the session knows which.
    /// </para>
    /// </remarks>
    bool StopSpeaking(ulong guildId);
}

/// <summary>
/// One answer being spoken in pieces, held for as long as there are pieces still to come.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists so that cutting the bot off drops the whole answer.</b> A piece is spoken through the
/// recital it belongs to, so a stop that lands between two of them still refuses the rest — where a
/// per-piece call would find nothing playing, report nothing stopped, and then carry on talking.
/// </para>
/// <para>
/// <b>Disposing closes it and speaks nothing.</b> A recital that has said everything it had is over,
/// and leaving it open would have the next interruption report that it stopped something.
/// </para>
/// </remarks>
public interface IVoiceRecital : IDisposable
{
    /// <summary>Whether this is still the answer being spoken, or has been cut off.</summary>
    bool Current { get; }

    /// <summary>
    /// Says the next piece — 48 kHz stereo signed 16-bit — after everything queued before it.
    /// </summary>
    /// <remarks>
    /// Pieces play in the order they were handed over: each waits for the one before it to finish, so
    /// a caller that hands them over in order hears them in order. A piece handed to a recital that
    /// has been cut off is refused rather than played, and that is checked again after the wait — the
    /// interruption that matters most is the one that lands while a piece is queued behind another.
    /// </remarks>
    Task<Result> SayAsync(byte[] pcm, CancellationToken ct = default);
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
/// <param name="Frames">
/// How many audio frames have arrived. Zero, with people in the channel and time gone by, is the one
/// failure this surface cannot otherwise show: connected and receiving nothing looks identical to
/// hearing somebody and not understanding them.
/// </param>
/// <param name="Others">How many people other than the bot are in the channel.</param>
/// <param name="For">How long it has been in there.</param>
public sealed record VoiceSession(
    ulong GuildId,
    ulong ChannelId,
    string ChannelName,
    DateTimeOffset JoinedAt,
    int Speakers,
    long Utterances,
    long Frames = 0,
    int Others = 0,
    TimeSpan For = default)
{
    /// <summary>
    /// Whether the bot is connected and has been sent nothing at all.
    /// </summary>
    /// <remarks>
    /// Only claimed once there has been long enough for silence to be surprising and somebody there
    /// to break it — a channel nobody is in, or one joined a second ago, is quiet for reasons that
    /// are not a fault. The causes are all outside this process: the bot server-deafened in the
    /// guild, a person's microphone muted or on push-to-talk, or a voice server that gave out a
    /// session and then routed no media.
    /// </remarks>
    public bool HearsNothing => Frames == 0 && Others > 0 && For > TimeSpan.FromSeconds(15);
}

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
