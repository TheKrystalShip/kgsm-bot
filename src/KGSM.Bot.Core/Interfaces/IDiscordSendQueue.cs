using KGSM.Bot.Core.Common;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// The one path out to Discord for everything the bot says on its own initiative.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rate-limit headroom is a host-wide resource, and this is what spends it.</b> Announcements fan
/// out across guilds, the status board edits a message per guild, installs create channels and
/// cleanup deletes messages — four producers with no knowledge of each other, each able to burst.
/// Throttled off the API, a bot loses everything else with the call that did it, which is the exact
/// failure that makes run state in a channel name impossible. One queue in front of all of it turns
/// four independent bursts into one paced stream.
/// </para>
/// <para>
/// <b>Interaction replies do not come through here</b> and must not. Discord gives three seconds to
/// acknowledge an interaction, and a reply queued behind a backlog is a reply that arrives after the
/// token is dead. Somebody waiting on their own slash command is also not the traffic that causes a
/// throttle. This queue is for what the bot says unprompted.
/// </para>
/// <para>
/// <b>It paces; Discord.Net still owns per-bucket waiting.</b> The client library reads the
/// rate-limit headers and knows which bucket a call belongs to, which is better information than
/// anything here has. The backoff below is the backstop for what escapes that — a 429 that arrives
/// anyway, a 500, a connection that drops — and its job is to slow the whole stream rather than spin
/// one call against a limit while the rest starve behind it.
/// </para>
/// </remarks>
public interface IDiscordSendQueue
{
    /// <summary>
    /// Queues a call that returns something the caller needs — the posted message, the created
    /// channel — and completes when it has been made.
    /// </summary>
    /// <typeparam name="T">What the call returns.</typeparam>
    /// <param name="what">
    /// What this call is, for the log. A short phrase naming the action and its target
    /// (<c>"announce crashed for factorio in 123"</c>), never a rendered message body.
    /// </param>
    /// <param name="lane">Which lane it waits in.</param>
    /// <param name="call">
    /// The Discord call. Invoked at most <c>MaxAttempts</c> times, so it must be repeatable, and it
    /// must not return null — <see cref="Result{T}"/> has no null success, so a call that can answer
    /// "there is no such thing" belongs on the overload below with the value captured in a closure.
    /// </param>
    /// <returns>
    /// What the call returned, or a failure describing why it was never made. <b>Never throws for a
    /// failed send</b> — a caller's failure path is a result, so one guild's dead channel cannot
    /// unwind the loop over the others.
    /// </returns>
    Task<Result<T>> SendAsync<T>(string what, SendLane lane, Func<Task<T>> call);

    /// <summary>
    /// Queues a call whose only outcome is whether it worked — a pin, a delete, an edit.
    /// </summary>
    /// <param name="what">What this call is, for the log.</param>
    /// <param name="lane">Which lane it waits in.</param>
    /// <param name="call">The Discord call. Invoked at most <c>MaxAttempts</c> times, so it must be repeatable.</param>
    /// <returns>Whether it was made, or why it was not.</returns>
    Task<Result> SendAsync(string what, SendLane lane, Func<Task> call);

    /// <summary>How many calls are waiting, by lane. For the status socket and the log.</summary>
    SendQueueDepth Depth { get; }
}

/// <summary>
/// Which lane a queued call waits in. Under backoff the queue drains
/// <see cref="Announcement"/> before it touches <see cref="Background"/>.
/// </summary>
/// <remarks>
/// The distinction is whether a delay costs anything. A crash notice delayed behind fifteen board
/// refreshes is the news arriving after the incident; a board refresh delayed behind the crash notice
/// is a message that says the same thing a moment later, and the next tick would have republished it
/// regardless. So the lane is not about importance in the abstract — it is about which of the two
/// still reads correctly late.
/// </remarks>
public enum SendLane
{
    /// <summary>
    /// Something a person is waiting to read: an announcement, the thread under it, the button on it.
    /// </summary>
    Announcement,

    /// <summary>
    /// Housekeeping that is correct whenever it lands: a status-board republish, a pin, an expiring
    /// announcement's deletion, a channel created or retired with an install.
    /// </summary>
    Background,
}

/// <summary>How much is waiting in each lane.</summary>
/// <param name="Announcements">Queued calls in the <see cref="SendLane.Announcement"/> lane.</param>
/// <param name="Background">Queued calls in the <see cref="SendLane.Background"/> lane.</param>
/// <param name="BackingOff">Whether the queue is currently holding off after a rate limit or a server error.</param>
public readonly record struct SendQueueDepth(int Announcements, int Background, bool BackingOff);
