using TheKrystalShip.KGSM.ComponentConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration options for KGSM
/// </summary>
[ConfigSection(Section)]
public class KgsmOptions
{
    public const string Section = "KGSM";

    /// <panel>Path to the KGSM executable. Everything the bot knows about this host's servers is read
    /// through it.</panel>
    [ConfigField("kgsmPath", "KGSM executable", Group = "kgsm", Type = ConfigType.Path, Risk = ConfigRisk.Wiring)]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Directory holding the <b>engine's</b> append-only event journal. Read-only and shared: the
    /// engine is its sole writer and any number of consumers read the same files, so nothing here
    /// belongs to the bot and nothing needs configuring on the engine side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This names one journal of several. The bot reads every producer's — the supervisor owns the
    /// crashes, the give-ups and player presence — and the others are found at their own state
    /// directories, needing no setting. This one has a setting because the engine's location is
    /// configurable and the scan looks only where a producer's own default puts it.
    /// </para>
    /// <para>
    /// The bot reads from the <b>tail</b> and keeps no position between runs. It announces events
    /// to Discord channels, and an announcement is only meaningful while it is current — replaying
    /// a backlog on restart would post "server started" for a server that started and stopped
    /// hours ago. Missing what happened during a restart is the correct trade: the durable record
    /// is the journals themselves, and this surface was never it.
    /// </para>
    /// </remarks>
    /// <panel>Directory holding the engine's event journal, which the bot reads so a channel's status
    /// updates the moment a server starts or stops. The supervisor's, the firewall's and the
    /// monitor's journals are found automatically and need no setting. Read-only and shared with
    /// every other consumer — nothing needs configuring on the engine side.</panel>
    [ConfigField("kgsmJournalDir", "KGSM event journal", Group = "kgsm", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string JournalDir { get; set; } = "/var/lib/kgsm/events";

    /// <summary>
    /// Control-socket path for the kgsm-watchdog supervisor daemon, used by the
    /// read-only supervision surface (the <c>/supervision</c> command). Native
    /// start/stop/restart are NOT issued here — they flow through <c>kgsm.sh</c>,
    /// which routes to the daemon itself when it is present. Defaults to the
    /// daemon's own default socket so the client always registers; an absent or
    /// unreachable daemon is handled gracefully at call time.
    /// </summary>
    /// <panel>The supervisor's control socket, which the bot starts and stops servers through.</panel>
    [ConfigField("watchdogSocketPath", "Watchdog socket", Group = "kgsm", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string WatchdogSocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>
    /// Where the bot serves its status snapshot over HTTP: gateway state, a row per configured guild,
    /// the channels it holds in each, and which announcements are switched on. <c>GET /status</c> on a
    /// unix socket, the same shape every other component on this host answers on.
    /// </summary>
    /// <remarks>
    /// This is also how the Control Panel gets a real health signal for this leaf. systemd liveness says
    /// the process is up, which is exactly the state the bot is in when a guild failed to populate and
    /// it can post nothing there — reading the snapshot proves the gateway and each guild, not just the
    /// process. Blank serves no status at all.
    /// </remarks>
    /// <panel>Where the bot publishes its status for the Control Panel to read — gateway state, each
    /// Discord server it is set up in, and its channel map. Leave blank to serve no status at all.</panel>
    [ConfigField("statusSocketPath", "Status socket", Group = "kgsm", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string StatusSocketPath { get; set; } = "/run/kgsm-bot/status.sock";

    /// <summary>Unix socket this bot answers for ITSELF on — its configuration, its unit, its journal
    /// and the commands it declares.</summary>
    /// <panel>Unix socket the Control Panel reaches this bot's own configuration and journal through.
    /// Moving it makes the panel read these settings off disk instead, which still works and cannot
    /// apply a change while the bot is up.</panel>
    [ConfigField("surfaceSocketPath", "Own-surface socket", Group = "kgsm", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string SurfaceSocketPath { get; set; } = "/run/kgsm-bot/surface.sock";

    /// <summary>The env file a configuration change made through the panel is written to.</summary>
    /// <panel>Where a setting changed in the Control Panel is written. It has to be a file this bot's
    /// unit loads with EnvironmentFile= — the panel checks, and reports the settings as read-only
    /// rather than writing a change nothing would read.</panel>
    [ConfigField("configOverridePath", "Override file", Group = "kgsm", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string ConfigOverridePath { get; set; } = "/var/lib/kgsm-api/leaf-overrides/bot.env";

    /// <summary>
    /// Control-socket path for the kgsm-firewall authority, which the bot asks whether a server's
    /// ports are actually reachable. Read-only: the bot opens nothing and closes nothing — ports are
    /// opened when a server starts and closed when it stops, and that is the watchdog's and the
    /// authority's business, not a chat surface's.
    /// </summary>
    /// <remarks>
    /// The authority is an optional sibling. An absent or unreachable socket costs the reachability
    /// half of <c>/connect</c> and nothing else, and is reported as unknown rather than as closed.
    /// </remarks>
    /// <panel>The firewall authority's control socket, which the bot asks whether a server's ports are
    /// actually reachable. It only ever reads.</panel>
    [ConfigField("firewallSocketPath", "Firewall socket", Group = "kgsm", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string FirewallSocketPath { get; set; } = "/run/kgsm-firewall/firewall.sock";

    public Dictionary<string, BlueprintSettings> Blueprints { get; set; } = new();
}

/// <summary>
/// Configuration options for blueprints
/// </summary>
public class BlueprintSettings
{
    public string OnlineTrigger { get; set; } = string.Empty;
}
