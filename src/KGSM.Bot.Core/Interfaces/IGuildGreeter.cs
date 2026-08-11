namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Says something when the bot is added to a Discord server that has not been set up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Silence and broken look identical from inside Discord.</b> A guild with no row in the store
/// hears nothing by design — that is the rule that stops a host broadcasting into any guild somebody
/// adds the bot to. But the person who just added it sees a bot that joined and then did nothing, and
/// has no way to tell that from a bot that is failing. One message closes that gap and costs nothing
/// afterwards: it is said once, on joining, and never again.
/// </para>
/// <para>
/// <b>It is not an announcement and grants nothing.</b> It names <c>/setup</c> and stops; running
/// that still needs KGSM admin, so a guild cannot talk itself into hearing about this host.
/// </para>
/// </remarks>
public interface IGuildGreeter
{
    /// <summary>
    /// Begins watching for joins. Called once the gateway is ready; calling it again is a no-op,
    /// because the ready handler fires on every reconnect and a second subscription would greet a
    /// guild twice.
    /// </summary>
    void Start();
}
