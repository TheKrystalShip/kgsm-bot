namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration options for KGSM
/// </summary>
public class KgsmOptions
{
    public const string Section = "KGSM";

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Directory holding the engine's append-only event journal, which the bot tails to learn
    /// when a server started, stopped, or was installed. Read-only and shared: the engine is the
    /// sole writer and any number of consumers read the same files, so nothing here belongs to
    /// the bot and nothing needs configuring on the engine side.
    /// </summary>
    /// <remarks>
    /// The bot reads from the <b>tail</b> and keeps no position between runs. It announces events
    /// to Discord channels, and an announcement is only meaningful while it is current — replaying
    /// a backlog on restart would post "server started" for a server that started and stopped
    /// hours ago. Missing what happened during a restart is the correct trade: the durable record
    /// is kgsm-monitor's, and this surface was never it.
    /// </remarks>
    public string JournalDir { get; set; } = "/var/lib/kgsm/events";

    /// <summary>
    /// Control-socket path for the kgsm-watchdog supervisor daemon, used by the
    /// read-only supervision surface (the <c>/supervision</c> command). Native
    /// start/stop/restart are NOT issued here — they flow through <c>kgsm.sh</c>,
    /// which routes to the daemon itself when it is present. Defaults to the
    /// daemon's own default socket so the client always registers; an absent or
    /// unreachable daemon is handled gracefully at call time.
    /// </summary>
    public string WatchdogSocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    public Dictionary<string, BlueprintSettings> Blueprints { get; set; } = new();
    public Dictionary<string, InstanceSettings> Instances { get; set; } = new();
}

/// <summary>
/// Configuration options for blueprints
/// </summary>
public class BlueprintSettings
{
    public string OnlineTrigger { get; set; } = string.Empty;
}

/// <summary>
/// Configuration options for instances
/// </summary>
public class InstanceSettings
{
    public string ChannelId { get; set; } = string.Empty;
    public string Blueprint { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"ChannelId: {ChannelId}, Blueprint: {Blueprint}";
    }
}
