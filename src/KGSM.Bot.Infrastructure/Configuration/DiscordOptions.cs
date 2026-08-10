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
