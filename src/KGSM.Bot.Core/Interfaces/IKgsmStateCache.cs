using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Caches the kgsm <em>inventory</em> — which instances exist and which
/// blueprints are installable — to avoid spawning a kgsm subprocess on every
/// message. This inventory changes only on install/uninstall, so it is safe to
/// cache with a TTL backstop plus event-driven invalidation.
///
/// Real-time state (whether a server is running, detailed status) is NOT cached
/// here; those go straight to the live services.
/// </summary>
public interface IKgsmStateCache
{
    /// <summary>Returns the installed instances, served from cache when fresh.</summary>
    Task<IReadOnlyDictionary<string, Instance>> GetInstancesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the installable blueprints, served from cache when fresh.</summary>
    Task<IReadOnlyDictionary<string, Blueprint>> GetBlueprintsAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks the instance inventory stale (e.g. after install/uninstall).</summary>
    void InvalidateInstances();

    /// <summary>Marks the blueprint inventory stale.</summary>
    void InvalidateBlueprints();
}
