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
}
