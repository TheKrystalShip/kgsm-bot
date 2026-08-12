using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Discord;
using Discord.WebSocket;

namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// Implementation of IDiscordNotificationService
/// </summary>
public class DiscordNotificationService : IDiscordNotificationService
{
    private readonly DiscordSocketClient _discordClient;
    private readonly IGuildStore _guilds;
    private readonly IDiscordSendQueue _queue;
    private readonly IIncidentTriage _triage;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(
        DiscordSocketClient discordClient,
        IGuildStore guilds,
        IDiscordSendQueue queue,
        IIncidentTriage triage,
        IOptions<DiscordOptions> options,
        ILogger<DiscordNotificationService> logger)
    {
        _discordClient = discordClient;
        _guilds = guilds;
        _queue = queue;
        _triage = triage;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Posts the announcement in every configured guild that follows the server it is about.
    /// </summary>
    /// <remarks>
    /// <b>A guild hears about this host because an admin ran <c>/setup</c> there</b>, never because
    /// the bot happens to be a member, and <b>only about the servers it follows</b>. One resolve and
    /// one send each, so a guild that has revoked a permission, lost its channel or gone unreachable
    /// is logged and the rest still hear about it — and the result counts the guilds reached against
    /// the guilds that <i>follow this server</i>. A bare success with one guild silently missed would
    /// be a fabricated status; counting a guild that opted out as missed is the same fault inverted,
    /// and would report a working filter as a partial failure on every announcement.
    /// </remarks>
    public async Task<Result> AnnounceAsync(ServerAnnouncement announcement)
    {
        if (!_options.Announce.IsEnabled(announcement.Kind))
        {
            _logger.LogDebug("Announcement {Kind} for {InstanceName} is switched off",
                announcement.Kind, announcement.InstanceName);
            return Result.Success();
        }

        IReadOnlyList<GuildTopology> guilds = _guilds.Configured();
        if (guilds.Count == 0)
        {
            _logger.LogDebug(
                "Not announcing {Kind} for {InstanceName}: no Discord server has been set up here yet",
                announcement.Kind, announcement.InstanceName);
            return Result.Success();
        }

        // A guild only hears about the servers it follows. Counted separately from the guilds
        // configured, because a guild that deliberately does not follow this server is not a guild
        // that was missed — folding the two together would report a working filter as a partial
        // failure on every announcement.
        List<GuildTopology> following =
            [.. guilds.Where(g => _guilds.Follows(g.GuildId, announcement.InstanceName))];

        if (following.Count == 0)
        {
            _logger.LogDebug(
                "Not announcing {Kind} for {InstanceName}: none of the {Configured} configured " +
                "Discord server(s) follow it",
                announcement.Kind, announcement.InstanceName, guilds.Count);
            return Result.Success();
        }

        string text = Render(announcement);
        List<string> failures = [];
        int reached = 0;

        foreach (GuildTopology topology in following)
        {
            Result result = await AnnounceInAsync(topology, announcement, text);
            if (result.IsSuccess)
                reached++;
            else
                failures.Add($"{topology.GuildId}: {result.Error}");
        }

        if (failures.Count == 0)
        {
            _logger.LogInformation(
                "Announced {Kind} for instance {InstanceName} in {Reached} guild(s) that follow it",
                announcement.Kind, announcement.InstanceName, reached);
            return Result.Success();
        }

        _logger.LogWarning(
            "Announced {Kind} for instance {InstanceName} in {Reached} of {Following} guild(s) — {Failures}",
            announcement.Kind, announcement.InstanceName, reached, following.Count, string.Join("; ", failures));

        return Result.Failure(
            $"announced in {reached} of {following.Count} guilds — {string.Join("; ", failures)}");
    }

    private async Task<Result> AnnounceInAsync(
        GuildTopology topology, ServerAnnouncement announcement, string text)
    {
        try
        {
            if (ResolveChannel(topology, announcement.InstanceName) is not ITextChannel channel)
                return Result.Failure("neither this server's channel nor the announcement channel can be seen");

            MessageComponent? components = ActionsFor(announcement);

            // The announcement lane: somebody is waiting to read this, and a crash notice that
            // arrives after fifteen board refreshes is the news arriving after the incident.
            Result<IUserMessage> posted = await _queue.SendAsync(
                $"announce {announcement.Kind} for {announcement.InstanceName} in {topology.GuildId}",
                SendLane.Announcement,
                async () => (IUserMessage)await channel.SendMessageAsync(
                    text,
                    allowedMentions: AllowedMentions.None,
                    components: components));

            if (posted.IsFailure)
                return posted;

            IUserMessage message = posted.Value!;

            if (await OpenIncidentThreadAsync(channel, message, announcement) is IThreadChannel thread)
                _triage.Begin(announcement, thread, topology.GuildId);

            if (_options.DeleteStatusMessageAfterDelay)
                ScheduleDeletion(message, announcement.InstanceName);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error announcing {Kind} for instance {InstanceName} in guild {GuildId}",
                announcement.Kind, announcement.InstanceName, topology.GuildId);
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// The buttons an announcement carries, or <see langword="null"/> when it carries none.
    /// </summary>
    /// <remarks>
    /// Which announcements are worth acting on is <see cref="AnnouncementActions"/>'s decision. The
    /// button itself grants nothing: it is a shortcut to <c>/restart</c>, authorized at the click
    /// against the same account store, and a name too long to fit an id simply gets no button rather
    /// than a truncated one pointing at a different server.
    /// </remarks>
    private MessageComponent? ActionsFor(ServerAnnouncement announcement)
    {
        if (!_options.ActionButtons)
            return null;

        if (!AnnouncementActions.OffersRestart(announcement.Kind))
            return null;

        if (!ServerActionIds.Fits(announcement.InstanceName))
        {
            _logger.LogDebug(
                "No restart button for {InstanceName}: the name does not fit a Discord component id.",
                announcement.InstanceName);
            return null;
        }

        return new ComponentBuilder()
            .WithButton("Restart", ServerActionIds.Restart(announcement.InstanceName), ButtonStyle.Primary)
            .Build();
    }

    /// <summary>
    /// Opens a thread under a crash announcement, so the conversation about it stays with it.
    /// </summary>
    /// <remarks>
    /// A crash is the one announcement people reply to, and a reply in a busy channel is a reply that
    /// scrolls away from the thing it is about. The thread is also where the assistant can be asked
    /// about it — the @-mention surface keys its conversation on the channel, so a thread is its own
    /// context rather than one more voice in the channel's.
    /// <para>
    /// <b>A missing permission costs the thread and nothing else.</b> The announcement is already
    /// posted by the time this runs, and it is the part that matters.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The thread, for whatever wants to say something in it — or <see langword="null"/> when this
    /// announcement opens none and when opening it failed. A caller therefore cannot post into a
    /// thread that does not exist, which is the only reason this hands one back at all.
    /// </returns>
    private async Task<IThreadChannel?> OpenIncidentThreadAsync(
        ITextChannel channel, IUserMessage message, ServerAnnouncement announcement)
    {
        if (!_options.IncidentThreads)
            return null;

        if (!AnnouncementActions.OpensThread(announcement.Kind))
            return null;

        try
        {
            // Checked before asking rather than after failing: a guild that has not granted this says
            // so once in the log, instead of throwing on every crash.
            if (channel is SocketTextChannel socket
                && !socket.Guild.CurrentUser.GetPermissions(socket).CreatePublicThreads)
            {
                _logger.LogDebug(
                    "No thread for {InstanceName}'s {Kind} in guild {GuildId}: the bot cannot create threads there.",
                    announcement.InstanceName, announcement.Kind, socket.Guild.Id);
                return null;
            }

            // Announcement lane, behind the message it hangs off: a thread for a conversation people
            // are having now is not housekeeping, and it is one call per crash rather than per event.
            Result<IThreadChannel> opened = await _queue.SendAsync(
                $"open a thread for {announcement.InstanceName}'s {announcement.Kind}",
                SendLane.Announcement,
                async () => (IThreadChannel)await channel.CreateThreadAsync(
                    ThreadName(announcement),
                    ThreadType.PublicThread,
                    ThreadArchiveDuration.OneDay,
                    message));

            if (opened.IsFailure)
            {
                _logger.LogWarning(
                    "Could not open a thread for {InstanceName}'s {Kind}: {Reason}. " +
                    "The announcement itself was posted.",
                    announcement.InstanceName, announcement.Kind, opened.Error);
                return null;
            }

            return opened.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not open a thread for {InstanceName}'s {Kind}; the announcement itself was posted.",
                announcement.InstanceName, announcement.Kind);
            return null;
        }
    }

    /// <summary>
    /// Names the thread after the server and the moment, so a second crash the same day does not
    /// produce two threads nobody can tell apart. Discord caps a thread name at 100 characters.
    /// </summary>
    private static string ThreadName(ServerAnnouncement announcement)
    {
        string verb = announcement.Kind == AnnouncementKind.Failed ? "down" : "crash";
        string name = $"{announcement.InstanceName} {verb} · {DateTimeOffset.UtcNow:MMM d HH:mm} UTC";
        return name.Length <= 100 ? name : name[..100];
    }

    /// <summary>
    /// The server's own channel in this guild, or the guild's announcement channel when it has none.
    /// </summary>
    /// <remarks>
    /// A server has a channel of its own only in a guild running a board, and only from the point the
    /// bot created it. Everything else — every server in a guild that announces into one channel, and
    /// a server that predates a board being turned on — routes to the channel the guild configured,
    /// which is why that one is required and the board is not. The message names the server, so it
    /// reads correctly either way.
    /// </remarks>
    /// <summary>
    /// Where this server reports in this guild: its own channel where it has a usable one, and the
    /// guild's announcement channel otherwise.
    /// </summary>
    /// <remarks>
    /// <b>A binding is a preference, not a requirement.</b> Somebody deleting a server's channel in
    /// Discord leaves the binding behind pointing at nothing, and treating that as the only place the
    /// server may report means every announcement about it is lost — silently, for as long as nobody
    /// notices. The announcement channel is the thing every guild is required to have and everything
    /// already falls back to, so it is what this falls back to as well.
    /// <para>
    /// The stale binding is <i>not</i> repaired here. Deciding a channel is really gone takes asking
    /// Discord, and doing that on the announcement path would spend a request per announcement to fix
    /// bookkeeping nobody is waiting on — <see cref="IDiscordChannelRegistry.ReconcileBindingsAsync"/>
    /// does it once, at startup.
    /// </para>
    /// </remarks>
    private ITextChannel? ResolveChannel(GuildTopology topology, string instanceName)
    {
        if (_guilds.ChannelFor(topology.GuildId, instanceName) is ulong bound)
        {
            if (_discordClient.GetChannel(bound) is ITextChannel own)
                return own;

            _logger.LogWarning(
                "{InstanceName}'s channel {ChannelId} in guild {GuildId} cannot be seen; announcing in " +
                "that guild's announcement channel instead.",
                instanceName, bound, topology.GuildId);
        }

        return _discordClient.GetChannel(topology.AnnounceChannelId) as ITextChannel;
    }

    /// <summary>
    /// Deletes the announcement once it has had its time, when the operator asked for that.
    /// </summary>
    /// <remarks>
    /// <b>Background lane, and deliberately.</b> A tidy-up is correct whenever it lands, so it waits
    /// behind anything a person is reading — and this is the one producer whose volume tracks the
    /// announcements exactly, which is what would otherwise double a burst's cost.
    /// <para>
    /// Not retried past a refusal: a message somebody already deleted answers 404, and asking again
    /// for it will not bring it back.
    /// </para>
    /// </remarks>
    private void ScheduleDeletion(IUserMessage message, string instanceName)
    {
        _ = DeleteLaterAsync();

        async Task DeleteLaterAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.DeleteStatusMessageDelaySeconds));

            Result deleted = await _queue.SendAsync(
                $"delete the expired announcement for {instanceName}",
                SendLane.Background,
                () => message.DeleteAsync());

            if (deleted.IsFailure)
            {
                _logger.LogWarning("Failed to delete announcement for instance {InstanceName}: {Reason}",
                    instanceName, deleted.Error);
            }
        }
    }

    /// <summary>
    /// Renders the message: a marker, the server, what happened, and the detail and actor the event
    /// carried. A detail or actor the event did not carry is left out — the sentence is shorter, not
    /// padded with "unknown".
    /// </summary>
    private string Render(ServerAnnouncement announcement)
    {
        string marker = MarkerFor(announcement.Kind);
        string text = $"{marker} **{announcement.InstanceName}** {VerbFor(announcement.Kind)}";

        if (announcement.Detail is not null)
        {
            text += $" — {announcement.Detail}";
        }

        if (Attribution(announcement.Actor) is string actor)
        {
            text += $" ({actor})";
        }

        return text;
    }

    /// <summary>
    /// The status markers the operator configured, host-wide rather than per guild: what this host
    /// announces is its own policy, and only where it lands is a guild's business. A kind with no
    /// configured marker of its own gets a literal.
    /// </summary>
    private string MarkerFor(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Started or AnnouncementKind.Ready or AnnouncementKind.Restarted
            => _options.Status.Online,
        AnnouncementKind.Stopped => _options.Status.Offline,
        AnnouncementKind.Uninstalled => _options.Status.Uninstalled,
        AnnouncementKind.Crashed => "💥",
        AnnouncementKind.Failed => "🛑",
        AnnouncementKind.UpdateAvailable => "🆕",
        AnnouncementKind.Updated => "⬆️",
        AnnouncementKind.Installed => "📦",
        AnnouncementKind.BackupCreated or AnnouncementKind.BackupRestored => "💾",
        AnnouncementKind.PlayerJoined => "➡️",
        AnnouncementKind.PlayerLeft => "⬅️",
        AnnouncementKind.PlayerKicked or AnnouncementKind.PlayerBanned => "🔨",
        AnnouncementKind.PlayerUnbanned => "🕊️",
        _ => "ℹ️",
    };

    /// <summary>
    /// What the message says happened. The <c>_ =></c> arm is a backstop for a kind added without a
    /// sentence, and it prints the enum name — readable, but not something a channel should ever see.
    /// </summary>
    internal static string VerbFor(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Started => "is starting",
        AnnouncementKind.Ready => "is ready to play",
        AnnouncementKind.Stopped => "has stopped",
        AnnouncementKind.Restarted => "was restarted",
        AnnouncementKind.Crashed => "crashed and is being restarted",
        AnnouncementKind.Failed => "is down — the supervisor gave up restarting it",
        AnnouncementKind.UpdateAvailable => "has an update available",
        AnnouncementKind.Updated => "was updated",
        AnnouncementKind.Installed => "was installed",
        AnnouncementKind.Uninstalled => "was uninstalled",
        AnnouncementKind.BackupCreated => "was backed up",
        AnnouncementKind.BackupRestored => "was restored from a backup",
        AnnouncementKind.PlayerJoined => "— player joined",
        AnnouncementKind.PlayerLeft => "— player left",
        AnnouncementKind.PlayerKicked => "— player kicked",
        AnnouncementKind.PlayerBanned => "— player banned",
        AnnouncementKind.PlayerUnbanned => "— player unbanned",
        _ => kind.ToString(),
    };

    /// <summary>
    /// How to credit the event's actor, or null to credit nobody.
    /// </summary>
    /// <remarks>
    /// The actor is carried verbatim from the engine and is not always a person: the supervisor
    /// acting on its own emits <c>system:watchdog</c>, and crediting that to "someone" would be a
    /// fabricated identity. A system actor is named as the system; a human actor keeps whatever
    /// string the engine recorded, prefix and all, because re-deriving a surface from it would be a
    /// second answer that could disagree with the audit trail.
    /// </remarks>
    private static string? Attribution(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) return null;

        string trimmed = actor.Trim();
        if (trimmed.StartsWith("system", StringComparison.OrdinalIgnoreCase))
        {
            return "automatic";
        }

        return $"by {trimmed}";
    }
}
