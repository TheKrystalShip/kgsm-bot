using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// <see cref="IKgsmStateCache"/> backed by the live kgsm services. Caches the
/// inventory with a TTL backstop and event-driven invalidation. On a refresh
/// failure, the last-known-good snapshot is kept rather than blanked.
/// </summary>
public class KgsmStateCache : IKgsmStateCache
{
    private static readonly IReadOnlyDictionary<string, Instance> EmptyInstances =
        new Dictionary<string, Instance>();
    private static readonly IReadOnlyDictionary<string, Blueprint> EmptyBlueprints =
        new Dictionary<string, Blueprint>();

    private readonly IServerInstanceService _instanceService;
    private readonly IBlueprintService _blueprintService;
    private readonly KgsmCacheOptions _options;
    private readonly ILogger<KgsmStateCache> _logger;

    private readonly SemaphoreSlim _instancesLock = new(1, 1);
    private readonly SemaphoreSlim _blueprintsLock = new(1, 1);

    private IReadOnlyDictionary<string, Instance>? _instances;
    private DateTime _instancesFetchedUtc = DateTime.MinValue;
    private volatile bool _instancesDirty = true;

    private IReadOnlyDictionary<string, Blueprint>? _blueprints;
    private DateTime _blueprintsFetchedUtc = DateTime.MinValue;
    private volatile bool _blueprintsDirty = true;

    public KgsmStateCache(
        IServerInstanceService instanceService,
        IBlueprintService blueprintService,
        IOptions<KgsmCacheOptions> options,
        ILogger<KgsmStateCache> logger)
    {
        _instanceService = instanceService;
        _blueprintService = blueprintService;
        _options = options.Value;
        _logger = logger;
    }

    public void InvalidateInstances() => _instancesDirty = true;
    public void InvalidateBlueprints() => _blueprintsDirty = true;

    public async Task<IReadOnlyDictionary<string, Instance>> GetInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsFresh(_instances, _instancesFetchedUtc, _instancesDirty, _options.InstancesTtlSeconds))
            return _instances!;

        await _instancesLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have refreshed while we waited.
            if (IsFresh(_instances, _instancesFetchedUtc, _instancesDirty, _options.InstancesTtlSeconds))
                return _instances!;

            var result = await _instanceService.GetAllAsync();
            if (result.IsSuccess && result.Value is not null)
            {
                _instances = result.Value;
                _instancesFetchedUtc = DateTime.UtcNow;
                _instancesDirty = false;
                _logger.LogDebug("Instance cache refreshed ({Count} instances)", _instances.Count);
            }
            else
            {
                _logger.LogWarning("Instance cache refresh failed ({Error}); serving {State}",
                    result.Error, _instances is null ? "empty" : "stale snapshot");
            }

            return _instances ?? EmptyInstances;
        }
        finally
        {
            _instancesLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, Blueprint>> GetBlueprintsAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsFresh(_blueprints, _blueprintsFetchedUtc, _blueprintsDirty, _options.BlueprintsTtlSeconds))
            return _blueprints!;

        await _blueprintsLock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh(_blueprints, _blueprintsFetchedUtc, _blueprintsDirty, _options.BlueprintsTtlSeconds))
                return _blueprints!;

            var result = await _blueprintService.GetAllAsync();
            if (result.IsSuccess && result.Value is not null)
            {
                _blueprints = result.Value;
                _blueprintsFetchedUtc = DateTime.UtcNow;
                _blueprintsDirty = false;
                _logger.LogDebug("Blueprint cache refreshed ({Count} blueprints)", _blueprints.Count);
            }
            else
            {
                _logger.LogWarning("Blueprint cache refresh failed ({Error}); serving {State}",
                    result.Error, _blueprints is null ? "empty" : "stale snapshot");
            }

            return _blueprints ?? EmptyBlueprints;
        }
        finally
        {
            _blueprintsLock.Release();
        }
    }

    private static bool IsFresh<T>(T? cache, DateTime fetchedUtc, bool dirty, int ttlSeconds)
        where T : class =>
        cache is not null && !dirty && DateTime.UtcNow - fetchedUtc < TimeSpan.FromSeconds(ttlSeconds);
}
