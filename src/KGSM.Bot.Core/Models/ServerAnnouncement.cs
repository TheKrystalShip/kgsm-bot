namespace KGSM.Bot.Core.Models;

/// <summary>
/// One thing worth telling a Discord channel about. Each value is a knob the operator can turn off
/// in the Control Panel, so this enum and the toggles in <c>Discord:Announce</c> are the same list
/// read two ways.
/// </summary>
/// <remarks>
/// Every value is sourced from an engine event the bot already reads off the journal. Nothing here
/// is derived, polled or inferred: a fact the journal does not carry is not a kind, and the bot
/// never reaches for a second source to answer from.
/// </remarks>
public enum AnnouncementKind
{
    /// <summary>The server's process was launched (<c>instance_started</c>).</summary>
    Started,

    /// <summary>The server finished loading and is playable (<c>instance_ready</c>).</summary>
    Ready,

    /// <summary>The server was stopped (<c>instance_stopped</c>).</summary>
    Stopped,

    /// <summary>An operator cycled the server (<c>instance_restarted</c>).</summary>
    Restarted,

    /// <summary>The supervisor found the process dead and is restarting it (<c>instance_crashed</c>).</summary>
    Crashed,

    /// <summary>The supervisor exhausted its restart attempts and left the server down (<c>instance_failed</c>).</summary>
    Failed,

    /// <summary>
    /// A newer game build exists upstream (<c>instance_update_available</c>). The engine records what
    /// each check found and emits only for a version it has not announced before, so this is one
    /// message per new build rather than one per check.
    /// </summary>
    UpdateAvailable,

    /// <summary>A new game build was applied (<c>instance_version_updated</c>).</summary>
    Updated,

    /// <summary>A server was installed (<c>instance_installed</c>).</summary>
    Installed,

    /// <summary>A server was uninstalled (<c>instance_uninstalled</c>).</summary>
    Uninstalled,

    /// <summary>A backup was written (<c>instance_backup_created</c>).</summary>
    BackupCreated,

    /// <summary>A backup was rolled back onto the server (<c>instance_backup_restored</c>).</summary>
    BackupRestored,

    /// <summary>A player connected (<c>instance_player_joined</c>).</summary>
    PlayerJoined,

    /// <summary>A player disconnected (<c>instance_player_left</c>).</summary>
    PlayerLeft,

    /// <summary>A player was disconnected by an operator (<c>instance_player_kicked</c>).</summary>
    PlayerKicked,

    /// <summary>A player was blocked from reconnecting (<c>instance_player_banned</c>).</summary>
    PlayerBanned,

    /// <summary>A block on a player was lifted (<c>instance_player_unbanned</c>).</summary>
    PlayerUnbanned,
}

/// <summary>
/// A normalized announcement, built from one engine event and carried to whichever channel reports
/// on <see cref="InstanceName"/>.
/// </summary>
/// <remarks>
/// The shape is deliberately flat. The event payloads it is built from disagree on almost
/// everything — a crash carries an exit code, an update carries two versions, a kick carries a
/// target and a command — so the one piece of prose each of them is worth is rendered where the
/// payload's type is still known, and travels here as <see cref="Detail"/>. That keeps the Discord
/// layer from switching over kgsm-lib's event classes, and keeps this type from growing a nullable
/// field per event kind.
/// </remarks>
/// <param name="Kind">Which announcement this is; also which toggle decides whether it is posted.</param>
/// <param name="InstanceName">The server the announcement is about, and the channel it routes to.</param>
/// <param name="Detail">
/// The one detail worth reading beside the headline (an exit code, a version pair, a player name),
/// already rendered. <see langword="null"/> when the event carries nothing material — never a
/// placeholder.
/// </param>
/// <param name="Actor">
/// Who triggered it, verbatim from the event (<c>heisen</c>, <c>discord:someone</c>,
/// <c>system:watchdog</c>). <see langword="null"/> when the emitter declared none — never
/// fabricated, and never re-derived into a surface it did not claim.
/// </param>
public sealed record ServerAnnouncement(
    AnnouncementKind Kind,
    string InstanceName,
    string? Detail = null,
    string? Actor = null);
