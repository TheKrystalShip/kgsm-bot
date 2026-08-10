using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Discord;
using Discord.WebSocket;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Discord;

/// <summary>
/// Serves the bot's status over a unix domain socket: one JSON line per connection, then close.
/// <para>
/// The same NDJSON-over-unix-socket shape kgsm-scheduler serves, and deliberately not HTTP — a Discord
/// bot has no web stack and there is exactly one consumer. Reading a line is also the leaf's health
/// check: it proves the gateway and each configured guild, where systemd liveness proves only that the
/// process exists. Those come apart in practice, and when they do this bot is running, connected, and
/// unable to post anything in the guild that came apart.
/// </para>
/// </summary>
/// <remarks>
/// The snapshot is built per connection rather than cached: it is asked for at human cadence by one
/// reader, and everything in it is an in-memory read off the live client. A blank socket path disables
/// the server outright, which is how a host that wants no status surface says so.
/// </remarks>
public sealed class StatusSocketServer(
    DiscordSocketClient client,
    IGuildStore guilds,
    IDiscordSendQueue queue,
    IOptions<KgsmOptions> kgsmOptions,
    IOptions<DiscordOptions> discordOptions,
    ILogger<StatusSocketServer> logger) : BackgroundService
{
    private readonly DiscordSocketClient _client = client;
    private readonly IGuildStore _guilds = guilds;
    private readonly IDiscordSendQueue _queue = queue;
    private readonly KgsmOptions _kgsm = kgsmOptions.Value;
    private readonly DiscordOptions _discord = discordOptions.Value;
    private readonly ILogger<StatusSocketServer> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string path = _kgsm.StatusSocketPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogInformation("Status socket disabled (no path configured)");
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            // The bot's job is Discord; failing to publish a status line must never stop it serving that.
            _logger.LogWarning(ex, "Could not prepare status socket at {Path} — no status will be served", path);
            return;
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(8);
            // 0660: the api runs in this socket's group and reads it; nothing else on the host needs to.
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not listen on status socket {Path} — no status will be served", path);
            return;
        }

        _logger.LogInformation("Status socket listening on {Path}", path);

        while (!ct.IsCancellationRequested)
        {
            Socket conn;
            try { conn = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Status socket accept error"); continue; }

            _ = Task.Run(() => ServeAsync(conn, ct), ct);
        }

        try { if (File.Exists(path)) File.Delete(path); } catch { /* shutting down */ }
    }

    private async Task ServeAsync(Socket conn, CancellationToken ct)
    {
        try
        {
            using (conn)
            {
                string json = JsonSerializer.Serialize(Snapshot(), BotStatusJsonContext.Default.BotStatus);
                await conn.SendAsync(Encoding.UTF8.GetBytes(json + "\n"), SocketFlags.None, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status socket client error");
        }
    }

    private BotStatus Snapshot()
    {
        // Latency is only meaningful once a heartbeat has completed; Discord.Net reports 0 until then,
        // and a 0ms gateway link would read as impossibly good rather than as unmeasured.
        int? latency = _client.ConnectionState == ConnectionState.Connected && _client.Latency > 0
            ? _client.Latency
            : null;

        return new BotStatus(
            ConnectionState: _client.ConnectionState.ToString(),
            LatencyMs: latency,
            CommandCount: CommandManifest.Build(Assembly.GetExecutingAssembly()).Gates.Sum(g => g.Value.Count),
            StoreAvailable: _guilds.Available,
            StoreUnavailableReason: _guilds.UnavailableReason,
            Guilds: [.. _guilds.Configured().Select(Describe)],
            Announcements: Switches(),
            SendQueue: Backlog());
    }

    /// <summary>
    /// What is waiting to go out. Read off the live queue, so it is the backlog at the moment the
    /// line was asked for rather than a figure kept somewhere and updated.
    /// </summary>
    private BotSendQueue Backlog()
    {
        SendQueueDepth depth = _queue.Depth;
        return new BotSendQueue(depth.Announcements, depth.Background, depth.BackingOff);
    }

    /// <summary>
    /// What one configured guild looks like from inside the gateway. Every field about Discord is read
    /// off the live client rather than repeated back out of the store: the store says what was asked
    /// for, and the interesting rows are the ones where the client disagrees with it.
    /// </summary>
    private BotGuild Describe(GuildTopology topology)
    {
        // The guild the client actually holds, not the one it was told to hold. Null here is the whole
        // point of this endpoint: connected, configured, no guild — and silent.
        SocketGuild? guild = _client.GetGuild(topology.GuildId);
        SocketChannel? announce = _client.GetChannel(topology.AnnounceChannelId);

        List<BotChannel> channels = [];
        foreach (GuildChannel binding in _guilds.ChannelsIn(topology.GuildId))
        {
            SocketChannel? channel = _client.GetChannel(binding.ChannelId);
            channels.Add(new BotChannel(
                Instance: binding.Instance,
                ChannelId: binding.ChannelId.ToString(),
                ChannelName: (channel as SocketTextChannel)?.Name,
                Visible: channel is not null));
        }

        return new BotGuild(
            GuildId: topology.GuildId.ToString(),
            Name: guild?.Name,
            MemberCount: guild?.MemberCount,
            AnnounceChannelId: topology.AnnounceChannelId.ToString(),
            AnnounceChannelName: (announce as SocketTextChannel)?.Name,
            AnnounceChannelVisible: announce is not null,
            BoardCategoryId: topology.BoardCategoryId?.ToString(),
            // Unresolved guild, unknown permission — reported as not held, because the consequence of
            // not holding it (no new server gets a channel) is exactly what an unresolved guild means.
            CanManageChannels: guild?.CurrentUser.GuildPermissions.ManageChannels ?? false,
            ConfiguredBy: topology.ConfiguredBy,
            Channels: channels);
    }

    private List<BotSwitch> Switches() => Switches(_discord.Announce);

    /// <summary>
    /// The announcement switches, read off the bound options rather than re-declared, so a state here
    /// cannot disagree with what the bot checks before posting.
    /// </summary>
    /// <remarks>
    /// <b>The keys match the leaf descriptor's</b>, which is what lets the panel put each state next to
    /// the control that edits it — and what makes a missing row invisible rather than obviously wrong:
    /// the switch simply never appears, and an operator concludes the bot cannot be told to stop. So
    /// the set is pinned by a test against the options type itself, one row per declared toggle.
    /// </remarks>
    internal static List<BotSwitch> Switches(AnnouncementOptions a)
    {
        return
        [
            new("announceStarted", "Server started", a.Started),
            new("announceReady", "Server ready to play", a.Ready),
            new("announceStopped", "Server stopped", a.Stopped),
            new("announceRestarted", "Server restarted", a.Restarted),
            new("announceCrashed", "Server crashed", a.Crashed),
            new("announceFailed", "Server gave up restarting", a.Failed),
            new("announceUpdateAvailable", "Game update available", a.UpdateAvailable),
            new("announceUpdated", "Game updated", a.Updated),
            new("announceInstalled", "Server installed", a.Installed),
            new("announceUninstalled", "Server uninstalled", a.Uninstalled),
            new("announceBackupCreated", "Backup created", a.BackupCreated),
            new("announceBackupRestored", "Backup restored", a.BackupRestored),
            new("announcePlayerJoined", "Player joined", a.PlayerJoined),
            new("announcePlayerLeft", "Player left", a.PlayerLeft),
            new("announceModeration", "Player moderated", a.Moderation),
        ];
    }
}
