using KGSM.Bot.Core.Interfaces;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// Adapts the bot's <see cref="IKgsmStateCache"/> to the assistant's
/// <see cref="IServerInventory"/> port, projecting the bot's domain models down to
/// the names the assistant needs. There is still exactly one cache (the bot's),
/// kept fresh by the kgsm event coordinator — this is just a read-only view of it.
/// </summary>
internal sealed class StateCacheInventory : IServerInventory
{
    private readonly IKgsmStateCache _cache;

    public StateCacheInventory(IKgsmStateCache cache) => _cache = cache;

    public async Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        var instances = await _cache.GetInstancesAsync(cancellationToken);
        return instances.ToDictionary(kv => kv.Key, kv => kv.Value.Blueprint);
    }

    public async Task<IReadOnlyCollection<string>> GetBlueprintNamesAsync(CancellationToken cancellationToken = default)
    {
        var blueprints = await _cache.GetBlueprintsAsync(cancellationToken);
        return blueprints.Keys.ToList();
    }
}
