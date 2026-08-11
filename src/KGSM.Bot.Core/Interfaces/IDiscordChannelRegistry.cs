using KGSM.Bot.Core.Common;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// The per-server channels the bot keeps in every guild that turned the board on.
/// </summary>
/// <remarks>
/// Bookkeeping, not announcing: a channel is created when a server is installed and retired when it
/// is uninstalled whether or not anything is announced about either. A guild with no board is skipped
/// entirely — it hears about every server in the one channel it configured.
/// </remarks>
public interface IDiscordChannelRegistry
{
    /// <summary>
    /// Give a server a channel in every guild running a board, creating it under that guild's
    /// category and recording the binding. A guild that already has a channel for this server keeps
    /// it.
    /// </summary>
    /// <returns>
    /// Failure names the guilds it could not be done in, so a partial result is never reported as a
    /// clean one.
    /// </returns>
    Task<Result> AddOrUpdateChannelAsync(string instanceName);

    /// <summary>
    /// Retire a server's channel everywhere it has one. Whether the channel itself is deleted is the
    /// operator's <c>Discord:RemoveChannelOnInstanceDeletion</c> switch; the binding goes either way.
    /// </summary>
    Task<Result> RemoveChannelAsync(string instanceName);

    /// <summary>
    /// Drop the bindings whose channel somebody deleted in Discord. Run once, after the gateway is
    /// ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A binding outlives the channel it names: somebody deletes the channel in Discord and nothing
    /// tells the bot. The announcement path survives that by falling back to the guild's announcement
    /// channel, so nothing is lost — but the binding is still a lie, it is still listed by
    /// <c>/setup show</c>, and it is what a reinstall of that server would try to reuse.
    /// </para>
    /// <para>
    /// <b>"Not visible" is not "deleted", and the difference decides whether a binding is destroyed.</b>
    /// A channel the bot has lost <c>View Channel</c> on is missing from the gateway cache exactly like
    /// one that no longer exists, and unbinding that one would orphan a live channel full of a
    /// server's history with nothing pointing at it. So a cache miss is <i>confirmed</i> against
    /// Discord before anything is forgotten, and only a channel Discord itself reports as gone is
    /// dropped.
    /// </para>
    /// </remarks>
    Task<Result> ReconcileBindingsAsync();
}
