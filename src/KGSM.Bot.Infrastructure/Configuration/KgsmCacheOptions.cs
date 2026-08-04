using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration for the kgsm inventory cache. TTLs are backstops; install/
/// uninstall events invalidate the instance cache immediately.
/// </summary>
[LeafSection(Section)]
public class KgsmCacheOptions
{
    public const string Section = "KgsmCache";

    /// <summary>How long instance inventory is served from cache before a refresh.</summary>
    /// <panel>How long the list of servers is reused before being re-read from KGSM.</panel>
    [LeafField("cacheInstancesTtlSec", "Server list cache", Group = "cache", Min = 0, Unit = "s")]
    public int InstancesTtlSeconds { get; set; } = 300;

    /// <summary>How long blueprint inventory is served from cache (rarely changes).</summary>
    /// <panel>How long the blueprint catalog is reused before being re-read from KGSM.</panel>
    [LeafField("cacheBlueprintsTtlSec", "Blueprint cache", Group = "cache", Min = 0, Unit = "s")]
    public int BlueprintsTtlSeconds { get; set; } = 600;
}
