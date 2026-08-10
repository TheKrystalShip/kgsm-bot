namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// The bot's own line in Discord's member list — <c>Watching 6 servers · 3 online</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only surface that says something without a channel.</b> Every other thing this bot does
/// needs somewhere to put it: a channel to announce in, a message to keep current, an interaction to
/// answer. Presence is attached to the bot rather than to a place, so it reaches a guild that has run
/// no <c>/setup</c> at all, and costs one call for every guild at once.
/// </para>
/// <para>
/// <b>It is a gateway update, and gateway presence updates are rate-limited hard</b> — a handful per
/// twenty seconds for the whole session, shared with nothing else and not covered by the queue that
/// paces this bot's REST traffic. So it runs on a slow fixed cadence and is never driven by an event:
/// a host reboot must not be able to spend the budget, and there is no version of this worth being
/// disconnected for.
/// </para>
/// </remarks>
public interface IBotPresence
{
    /// <summary>
    /// Begins keeping the presence current. Called once the gateway is ready; calling it again is a
    /// no-op, because the ready handler fires on every reconnect and a second loop would double the
    /// rate.
    /// </summary>
    void Start();
}
