namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration for the kgsm inventory cache. TTLs are backstops; install/
/// uninstall events invalidate the instance cache immediately.
/// </summary>
public class KgsmCacheOptions
{
    public const string Section = "KgsmCache";

    /// <summary>How long instance inventory is served from cache before a refresh.</summary>
    public int InstancesTtlSeconds { get; set; } = 300;

    /// <summary>How long blueprint inventory is served from cache (rarely changes).</summary>
    public int BlueprintsTtlSeconds { get; set; } = 600;
}
