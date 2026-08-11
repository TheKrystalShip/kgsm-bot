using System.Collections.Concurrent;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <inheritdoc cref="IBackupInsight" />
/// <remarks>
/// <para>
/// <b>Cached because the whole-host read is a kgsm process per server</b>, and the live status
/// message would spend one per server on every publish for a number that changes when a backup is
/// taken and at no other time. The cache holds the engine's timestamp, never a computed age, so a
/// stale entry still renders correctly — and the engine's own backup events drop the entry, so the
/// TTL is a backstop rather than the mechanism.
/// </para>
/// <para>
/// <b>A read that failed is not cached.</b> Remembering "could not look" would keep saying it after
/// the reason had gone away, and the next caller re-reading costs one process.
/// </para>
/// </remarks>
public sealed class BackupInsight : IBackupInsight
{
    private readonly IKgsmStateCache _cache;
    private readonly IServerInstanceService _instances;
    private readonly KgsmCacheOptions _options;
    private readonly ILogger<BackupInsight> _logger;

    private readonly ConcurrentDictionary<string, Entry> _latest = new(StringComparer.OrdinalIgnoreCase);

    public BackupInsight(
        IKgsmStateCache cache,
        IServerInstanceService instances,
        IOptions<KgsmCacheOptions> options,
        ILogger<BackupInsight> logger)
    {
        _cache = cache;
        _instances = instances;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<InstanceBackup>>> ListAsync(string instanceName) =>
        _instances.GetBackupsAsync(instanceName);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, InstanceBackup?>> LatestAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Instance> inventory;
        try
        {
            inventory = await _cache.GetInstancesAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The instance inventory could not be read for the backup summary.");
            return new Dictionary<string, InstanceBackup?>();
        }

        // Each miss spawns a kgsm process, so the misses run together rather than in sequence.
        (string Name, bool Known, InstanceBackup? Latest)[] read = await Task.WhenAll(
            inventory.Keys.Select(async name =>
            {
                (bool known, InstanceBackup? latest) = await LatestForAsync(name);
                return (name, known, latest);
            }));

        // Only the servers that could be read, and each read says for itself whether it could be —
        // re-deriving that from the cache afterwards would drop a server that a concurrent
        // invalidation had just removed, reporting a successful read as an unanswerable one.
        return read
            .Where(entry => entry.Known)
            .ToDictionary(entry => entry.Name, entry => entry.Latest, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Invalidate(string instanceName)
    {
        if (_latest.TryRemove(instanceName, out _))
            _logger.LogDebug("Dropped the cached backup summary for {InstanceName}.", instanceName);
    }

    /// <returns>
    /// Whether the answer is known at all, and the newest backup when it is. Those are two facts and
    /// a nullable cannot carry both: null alone would make "never backed up" and "could not look"
    /// the same value.
    /// </returns>
    private async Task<(bool Known, InstanceBackup? Latest)> LatestForAsync(string instanceName)
    {
        if (_latest.TryGetValue(instanceName, out Entry entry) && !entry.IsStale(Ttl))
            return (true, entry.Latest);

        Result<IReadOnlyList<InstanceBackup>> backups = await _instances.GetBackupsAsync(instanceName);
        if (backups.IsFailure)
        {
            // Deliberately not cached: remembering "could not look" would keep saying it after the
            // reason had gone, and re-reading costs one process.
            _logger.LogDebug("Could not read {InstanceName}'s backups: {Reason}", instanceName, backups.Error);
            _latest.TryRemove(instanceName, out _);
            return (false, null);
        }

        InstanceBackup? latest = backups.Value!.Count > 0 ? backups.Value[0] : null;
        _latest[instanceName] = new Entry(latest, DateTimeOffset.UtcNow);
        return (true, latest);
    }

    /// <summary>
    /// The backstop. Events are what actually keep this current; this only bounds how wrong it can be
    /// if one is missed, and the value it guards ages correctly in the meantime either way.
    /// </summary>
    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(_options.InstancesTtlSeconds, 60));

    private readonly record struct Entry(InstanceBackup? Latest, DateTimeOffset Read)
    {
        public bool IsStale(TimeSpan ttl) => DateTimeOffset.UtcNow - Read > ttl;
    }
}
