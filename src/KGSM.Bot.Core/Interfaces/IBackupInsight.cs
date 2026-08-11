using KGSM.Bot.Core.Common;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// What this host can say about its backups.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place the backup facts come from</b>, like the player roster: the commands and the live
/// status message read this, so a board saying a server was backed up yesterday and a command saying
/// it never was cannot both be on screen.
/// </para>
/// <para>
/// <b>Nothing here computes an age.</b> It carries the <i>timestamp</i> the engine recorded, and the
/// age is worked out wherever it is rendered. That is what makes the cache honest: a cached timestamp
/// still yields a correct age however old the cache is, where a cached age would silently stop
/// counting.
/// </para>
/// </remarks>
public interface IBackupInsight
{
    /// <summary>
    /// Every backup one server has, newest first. Read fresh — this answers somebody who asked.
    /// </summary>
    /// <remarks>An empty list is a real answer: the server has never been backed up.</remarks>
    Task<Result<IReadOnlyList<InstanceBackup>>> ListAsync(string instanceName);

    /// <summary>
    /// The newest backup of every installed server, for surfaces that show the whole host.
    /// </summary>
    /// <returns>
    /// <b>A key is present only for a server whose backups were actually read.</b> A present key with
    /// a <see langword="null"/> value is a server read successfully that has no backups; an absent key
    /// is a server this host could not answer for. A renderer must tell those apart — "never backed
    /// up" and "could not look" are different things to put in front of an operator.
    /// </returns>
    Task<IReadOnlyDictionary<string, InstanceBackup?>> LatestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forget what is cached for one server, because something just changed it.
    /// </summary>
    /// <remarks>
    /// Driven by the engine's own backup events rather than by a timer: the set of backups changes
    /// when the engine says it did, and at no other time.
    /// </remarks>
    void Invalidate(string instanceName);
}
