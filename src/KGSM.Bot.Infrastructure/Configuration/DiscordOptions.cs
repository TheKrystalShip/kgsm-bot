using KGSM.Bot.Core.Models;

using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration options for Discord — this host's own policy, not any one Discord server's.
/// </summary>
/// <remarks>
/// Where announcements land is not here: which guilds hear about this host, the channel each of them
/// takes announcements in, and whether it keeps a channel per server are all set with <c>/setup</c>
/// from inside Discord and held in the guild store. What this host announces at all, and how a
/// message looks when it does, is host policy and lives here.
/// </remarks>
[LeafSection(Section)]
public class DiscordOptions
{
    public const string Section = "Discord";

    /// <panel>Token the bot signs in to Discord with. Without a valid one it cannot connect at
    /// all.</panel>
    [LeafField("discordToken", "Bot token", Group = "discord", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string Token { get; set; } = string.Empty;

    /// <panel>Whether uninstalling a server also deletes its Discord channel, taking that channel's
    /// history with it. Off, the channel is left behind.</panel>
    [LeafField("removeChannelOnUninstall", "Delete channel with the server", Group = "channels",
        Risk = LeafRisk.Destructive)]
    public bool RemoveChannelOnInstanceDeletion { get; set; } = false;

    /// <summary>
    /// The address the bot tells people to connect to. Set it when this host is reached by a name
    /// rather than by its IP.
    /// </summary>
    /// <remarks>
    /// A host cannot discover this: a DNS record pointing at it is a fact about the world, not about
    /// the machine. So an operator-set value is authoritative and is used verbatim; left blank, the
    /// bot falls back to the external IP the host measured, which is correct at the moment it is read
    /// and is not a promise.
    /// </remarks>
    /// <panel>The address the bot tells people to connect to — a domain name, if this host has one.
    /// Left blank, it uses the external IP address the host measures for itself.</panel>
    [LeafField("publicAddress", "Public address", Group = "connect")]
    public string PublicAddress { get; set; } = string.Empty;

    /// <summary>
    /// Whether an announcement about a server that is down carries a button to restart it.
    /// </summary>
    /// <remarks>
    /// The button is a shortcut to the slash command and nothing more: it is authorized at the click
    /// against the same account store, runs the same path, and stamps the same provenance. Turning it
    /// off costs the shortcut, never the safety — nobody gains authority from a button.
    /// </remarks>
    /// <panel>Whether an announcement about a server that is down carries a button to restart it.
    /// Pressing it needs the same permission the command does.</panel>
    [LeafField("announcementActions", "Buttons on announcements", Group = "announcements")]
    public bool ActionButtons { get; set; } = true;

    /// <summary>
    /// Whether a crash opens a thread under its announcement for the conversation about it.
    /// </summary>
    /// <remarks>
    /// Needs <c>Create Public Threads</c> in the channel; without it the announcement is posted
    /// plainly and nothing is lost. Threads auto-archive, so nothing accumulates.
    /// </remarks>
    /// <panel>Whether a crash opens a thread under its announcement, so the conversation about it
    /// stays with it instead of scrolling the channel. Needs permission to create threads.</panel>
    [LeafField("incidentThreads", "Threads for crashes", Group = "announcements")]
    public bool IncidentThreads { get; set; } = true;

    /// <summary>
    /// The floor on how often a guild's live status message is edited.
    /// </summary>
    /// <remarks>
    /// This is what turns a burst into one edit. A host reboot produces one event per server in the
    /// same second, and spending an edit on each is how a bot gets throttled off the API — losing the
    /// announcements with it. Everything that arrives inside the window is published together.
    /// </remarks>
    /// <panel>How long the bot waits before editing the live status message again, so a burst of
    /// changes becomes one edit instead of one each.</panel>
    [LeafField("statusMessageMinIntervalSec", "Status message floor", Group = "status",
        Min = 5, Unit = "s")]
    public int StatusMessageMinIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How often the live status message is republished even when nothing changed.
    /// </summary>
    /// <remarks>
    /// The backstop, not the mechanism: the message is driven by events, and this only catches what
    /// no event describes — a server stopped outside the engine, a missed journal line, a message
    /// somebody deleted.
    /// </remarks>
    /// <panel>How often the live status message is refreshed even when nothing has happened, to catch
    /// anything no event reported.</panel>
    [LeafField("statusMessageRefreshSec", "Status message refresh", Group = "status",
        Min = 60, Unit = "s")]
    public int StatusMessageRefreshSeconds { get; set; } = 900;

    /// <summary>
    /// How old a server's newest backup may be before the live status message says so.
    /// </summary>
    /// <remarks>
    /// <b>The board stays quiet while backups are current and speaks when they are not.</b> Printing
    /// an age beside all sixteen servers would make the one that matters invisible among fifteen that
    /// do not — so the marker appears only past this age, and for a server with no backup at all.
    /// Zero shows it for every server, for an operator who would rather see the number always.
    /// </remarks>
    /// <panel>How old a server's newest backup has to be before the live status message flags it. A
    /// server with no backup at all is always flagged. Set to 0 to show the age for every server.</panel>
    [LeafField("backupStaleAfterHours", "Flag backups older than", Group = "status",
        Min = 0, Unit = "h")]
    public int BackupStaleAfterHours { get; set; } = 48;

    /// <summary>
    /// Whether the read commands answer only the person who ran them.
    /// </summary>
    /// <remarks>
    /// <c>/status</c>, <c>/list</c>, <c>/is-active</c> and <c>/supervision</c> are one person's
    /// question, and a busy channel does not need everyone's status checks in its scrollback. The
    /// commands whose whole point is to be shared — <c>/connect</c> above all — are deliberately not
    /// covered by this, and <c>/logs</c> is always private whatever this says.
    /// </remarks>
    /// <panel>Whether answers to "what's the status" commands are shown only to the person who asked,
    /// instead of to the whole channel. Sharing commands like /connect are unaffected.</panel>
    [LeafField("ephemeralReads", "Keep status answers private", Group = "channels")]
    public bool EphemeralReads { get; set; } = true;

    /// <summary>
    /// Whether the bot shows this host in its own Discord presence.
    /// </summary>
    /// <remarks>
    /// The one thing this bot says without a channel to say it in: it reaches a guild that has never
    /// run <c>/setup</c>, and one update covers every guild at once. Off, the bot appears with no
    /// activity and nothing else changes.
    /// </remarks>
    /// <panel>Whether the bot shows how many servers are up beside its name in the member list. It
    /// needs no channel and no setup, and every Discord server sees the same line.</panel>
    [LeafField("presence", "Show the host in the member list", Group = "status")]
    public bool Presence { get; set; } = true;

    /// <summary>
    /// How often the presence is recomposed.
    /// </summary>
    /// <remarks>
    /// A gateway presence update is limited to a handful per twenty seconds for the whole session, and
    /// that budget is not the one the send queue paces. This cadence is the only thing protecting it,
    /// which is why the presence is never driven by an event: the line is recomposed on the tick and
    /// sent only when it changed.
    /// </remarks>
    /// <panel>How often the bot re-checks the host to update the line beside its name. Set it low and
    /// Discord will start refusing the updates.</panel>
    [LeafField("presenceRefreshSec", "Presence refresh", Group = "status",
        Min = 20, Unit = "s", DependsOn = "presence")]
    public int PresenceRefreshSeconds { get; set; } = 60;

    /// <summary>
    /// The floor between two calls the bot makes to Discord on its own initiative.
    /// </summary>
    /// <remarks>
    /// This is what keeps a limit from being reached at all, and it is worth more than any backoff —
    /// a 429 has already spent the request that earned it. Announcements fan out across guilds, the
    /// board edits a message per guild, and installs create channels; without a floor those are
    /// several producers bursting at once with no knowledge of each other. An idle bot pays nothing:
    /// the floor is a gap between calls, not a delay on every one.
    /// </remarks>
    /// <panel>How long the bot waits between two messages it sends on its own, so a burst is spread
    /// out instead of arriving at once and getting the bot throttled.</panel>
    [LeafField("sendQueueMinIntervalMs", "Send floor", Group = "limits",
        Min = 0, Max = 5000, Unit = "ms", Risk = LeafRisk.Wiring)]
    public int SendQueueMinIntervalMs { get; set; } = 200;

    /// <summary>
    /// How many calls may wait in each of the two lanes before further ones are refused.
    /// </summary>
    /// <remarks>
    /// An unbounded queue in front of a rate limit is a memory leak with a delay on it. Overflow is
    /// refused and reported, never dropped quietly: a caller's own accounting shows the guild it did
    /// not reach, because a bot announcing nothing must not look like a host where nothing happened.
    /// </remarks>
    /// <panel>How many messages may wait to be sent before the bot starts refusing new ones and
    /// saying so. Reaching this means Discord is not keeping up with this host.</panel>
    [LeafField("sendQueueCapacity", "Send queue size", Group = "limits",
        Min = 16, Risk = LeafRisk.Wiring)]
    public int SendQueueCapacity { get; set; } = 500;

    /// <summary>
    /// How many times one call is attempted before it is given up on.
    /// </summary>
    /// <remarks>
    /// Only a rate limit, a server error or a dropped connection is retried at all. A refusal, a
    /// missing channel or a malformed request is the answer rather than a hiccup, and is failed on
    /// the first attempt however high this is set.
    /// </remarks>
    /// <panel>How many times the bot re-tries a message Discord could not take, before giving up on
    /// it. Only a temporary failure is re-tried.</panel>
    [LeafField("sendQueueMaxAttempts", "Send attempts", Group = "limits",
        Min = 1, Max = 10, Risk = LeafRisk.Wiring)]
    public int SendQueueMaxAttempts { get; set; } = 4;

    /// <summary>The first hold-off after a rate limit or a server error; it doubles from here.</summary>
    /// <panel>How long the bot pauses everything it is sending after Discord refuses a message for a
    /// temporary reason. It doubles each time until the ceiling below.</panel>
    [LeafField("sendQueueBackoffMs", "Backoff", Group = "limits",
        Min = 100, Unit = "ms", Risk = LeafRisk.Wiring)]
    public int SendQueueBackoffMs { get; set; } = 1000;

    /// <summary>The ceiling the doubling hold-off stops at.</summary>
    /// <panel>The longest the bot will pause between re-tries, however many times sending has
    /// failed.</panel>
    [LeafField("sendQueueMaxBackoffMs", "Backoff ceiling", Group = "limits",
        Min = 1000, Unit = "ms", Risk = LeafRisk.Wiring)]
    public int SendQueueMaxBackoffMs { get; set; } = 60000;

    public StatusOptions Status { get; set; } = new();
    public AnnouncementOptions Announce { get; set; } = new();
    /// <panel>Whether announcements are deleted again after a while, so a busy channel does not fill
    /// with them. On, the channel keeps no record of what happened.</panel>
    [LeafField("deleteStatusMessages", "Clean up announcements", Group = "channels")]
    public bool DeleteStatusMessageAfterDelay { get; set; } = false;
    /// <panel>How long an announcement stays before it is deleted.</panel>
    [LeafField("deleteStatusMessagesAfterSec", "Announcement lifetime", Group = "channels",
        Min = 1, Unit = "s", DependsOn = "deleteStatusMessages")]
    public int DeleteStatusMessageDelaySeconds { get; set; } = 300;
}

/// <summary>
/// Which events the bot announces. One switch per <see cref="Core.Models.AnnouncementKind"/>, so
/// the operator turns off the noise their guild does not want without losing the rest.
/// </summary>
/// <remarks>
/// The defaults are the events an operator would be surprised to miss — a server going up or down,
/// a crash, an update, an install. Everything that fires on its own cadence rather than on an
/// operator's action starts off: a busy server produces a join and a leave per player per session,
/// and <c>instance_ready</c> follows every start (including the supervisor's own restarts), so both
/// would double the traffic in a channel for a fact the operator can already see.
/// </remarks>
public class AnnouncementOptions
{
    /// <panel>A server's process was launched.</panel>
    [LeafField("announceStarted", "Server started", Group = "announcements")]
    public bool Started { get; set; } = true;

    /// <panel>A server finished loading and is playable. Follows every start, including the ones the
    /// supervisor makes on its own, so leaving it off keeps a channel to one message per start.</panel>
    [LeafField("announceReady", "Server ready to play", Group = "announcements")]
    public bool Ready { get; set; } = false;

    /// <panel>A server was stopped.</panel>
    [LeafField("announceStopped", "Server stopped", Group = "announcements")]
    public bool Stopped { get; set; } = true;

    /// <panel>Someone cycled a server deliberately.</panel>
    [LeafField("announceRestarted", "Server restarted", Group = "announcements")]
    public bool Restarted { get; set; } = true;

    /// <panel>A server died unexpectedly and the supervisor is restarting it. Announced once per
    /// crash, not once per restart attempt.</panel>
    [LeafField("announceCrashed", "Server crashed", Group = "announcements")]
    public bool Crashed { get; set; } = true;

    /// <panel>The supervisor ran out of restart attempts and left a server down. This is the one
    /// that means somebody has to go look.</panel>
    [LeafField("announceFailed", "Server gave up restarting", Group = "announcements")]
    public bool Failed { get; set; } = true;

    /// <panel>A newer game build was released for a server. Announced once per build, not once per
    /// check, and only for servers this host actually checks.</panel>
    [LeafField("announceUpdateAvailable", "Game update available", Group = "announcements")]
    public bool UpdateAvailable { get; set; } = true;

    /// <panel>A new game build was applied to a server.</panel>
    [LeafField("announceUpdated", "Game updated", Group = "announcements")]
    public bool Updated { get; set; } = true;

    /// <panel>A new server was installed.</panel>
    [LeafField("announceInstalled", "Server installed", Group = "announcements")]
    public bool Installed { get; set; } = true;

    /// <panel>A server was uninstalled.</panel>
    [LeafField("announceUninstalled", "Server uninstalled", Group = "announcements")]
    public bool Uninstalled { get; set; } = true;

    /// <panel>A backup of a server was written.</panel>
    [LeafField("announceBackupCreated", "Backup created", Group = "announcements")]
    public bool BackupCreated { get; set; } = false;

    /// <panel>A backup was rolled back onto a server, replacing what was there.</panel>
    [LeafField("announceBackupRestored", "Backup restored", Group = "announcements")]
    public bool BackupRestored { get; set; } = true;

    /// <panel>A player connected. One message per join — busy on a popular server.</panel>
    [LeafField("announcePlayerJoined", "Player joined", Group = "announcements")]
    public bool PlayerJoined { get; set; } = false;

    /// <panel>A player disconnected. One message per leave.</panel>
    [LeafField("announcePlayerLeft", "Player left", Group = "announcements")]
    public bool PlayerLeft { get; set; } = false;

    /// <panel>A player was kicked, banned, or had a ban lifted.</panel>
    [LeafField("announceModeration", "Player kicked or banned", Group = "announcements")]
    public bool Moderation { get; set; } = true;

    /// <summary>
    /// Whether this kind of announcement is switched on. The three moderation kinds share one
    /// switch: an operator who wants to hear about bans wants to hear about kicks.
    /// </summary>
    /// <remarks>
    /// A kind with no case here is a kind somebody added without deciding whether an operator may
    /// turn it off. Announcing it is the safer half of that mistake — a message nobody asked for is
    /// visible and gets fixed, silence is not — and the test over this method is what stops it
    /// reaching a channel in the first place.
    /// </remarks>
    public bool IsEnabled(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Started => Started,
        AnnouncementKind.Ready => Ready,
        AnnouncementKind.Stopped => Stopped,
        AnnouncementKind.Restarted => Restarted,
        AnnouncementKind.Crashed => Crashed,
        AnnouncementKind.Failed => Failed,
        AnnouncementKind.UpdateAvailable => UpdateAvailable,
        AnnouncementKind.Updated => Updated,
        AnnouncementKind.Installed => Installed,
        AnnouncementKind.Uninstalled => Uninstalled,
        AnnouncementKind.BackupCreated => BackupCreated,
        AnnouncementKind.BackupRestored => BackupRestored,
        AnnouncementKind.PlayerJoined => PlayerJoined,
        AnnouncementKind.PlayerLeft => PlayerLeft,
        AnnouncementKind.PlayerKicked => Moderation,
        AnnouncementKind.PlayerBanned => Moderation,
        AnnouncementKind.PlayerUnbanned => Moderation,
        _ => true,
    };
}

/// <summary>
/// Configuration options for status messages
/// </summary>
public class StatusOptions
{
    /// <panel>Shown beside a server that is running.</panel>
    [LeafField("statusOnline", "Running marker", Group = "channels")]
    public string Online { get; set; } = string.Empty;

    /// <panel>Shown beside a server that is stopped.</panel>
    [LeafField("statusOffline", "Stopped marker", Group = "channels")]
    public string Offline { get; set; } = string.Empty;

    /// <panel>Shown beside a server that is no longer installed.</panel>
    [LeafField("statusUninstalled", "Uninstalled marker", Group = "channels")]
    public string Uninstalled { get; set; } = string.Empty;
}
