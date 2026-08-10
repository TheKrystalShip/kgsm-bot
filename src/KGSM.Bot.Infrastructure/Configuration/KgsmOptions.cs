using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration options for KGSM
/// </summary>
[LeafSection(Section)]
public class KgsmOptions
{
    public const string Section = "KGSM";

    /// <panel>Path to the KGSM executable. Everything the bot knows about this host's servers is read
    /// through it.</panel>
    [LeafField("kgsmPath", "KGSM executable", Group = "kgsm", Type = LeafType.Path, Risk = LeafRisk.Wiring)]
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
    /// <panel>Directory holding the engine's event journal, which the bot reads so a channel's status
    /// updates the moment a server starts or stops. Read-only and shared with every other consumer —
    /// nothing needs configuring on the engine side.</panel>
    [LeafField("kgsmJournalDir", "KGSM event journal", Group = "kgsm", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
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
    [LeafField("watchdogSocketPath", "Watchdog socket", Group = "kgsm", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string WatchdogSocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>
    /// Where the bot serves its one-line status snapshot: gateway state, a row per configured guild,
    /// the channels it holds in each, and which announcements are switched on. One JSON line per
    /// connection, then close — the same NDJSON-over-unix-socket shape kgsm-scheduler serves, and
    /// deliberately not HTTP: a Discord bot carries no web stack, and a tiny private protocol is
    /// enough for the one consumer.
    /// </summary>
    /// <remarks>
    /// This is also how the Control Panel gets a real health signal for this leaf. systemd liveness says
    /// the process is up, which is exactly the state the bot is in when a guild failed to populate and
    /// it can post nothing there — reading a status line proves the gateway and each guild, not just the
    /// process. Blank disables the server entirely.
    /// </remarks>
    /// <panel>Where the bot publishes its status for the Control Panel to read — gateway state, each
    /// Discord server it is set up in, and its channel map. Leave blank to serve no status at all.</panel>
    [LeafField("statusSocketPath", "Status socket", Group = "kgsm", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string StatusSocketPath { get; set; } = "/run/kgsm-bot/status.sock";

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
    [LeafField("firewallSocketPath", "Firewall socket", Group = "kgsm", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
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
