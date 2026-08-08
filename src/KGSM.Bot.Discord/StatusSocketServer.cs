using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Discord;
using Discord.WebSocket;
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
/// check: it proves the gateway and the resolved guild, where systemd liveness proves only that the
/// process exists. Those come apart in practice, and when they do this bot is running, connected, and
/// unable to post anything.
/// </para>
/// </summary>
/// <remarks>
/// The snapshot is built per connection rather than cached: it is asked for at human cadence by one
/// reader, and everything in it is an in-memory read off the live client. A blank socket path disables
/// the server outright, which is how a host that wants no status surface says so.
/// </remarks>
public sealed class StatusSocketServer(
    DiscordSocketClient client,
    IOptions<KgsmOptions> kgsmOptions,
    IOptions<DiscordOptions> discordOptions,
    ILogger<StatusSocketServer> logger) : BackgroundService
{
    private readonly DiscordSocketClient _client = client;
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
        // The guild the client actually holds, not the one it was told to hold. Null here with a
        // non-null configured id is the whole point of this endpoint: connected, configured, no guild.
        SocketGuild? guild = _discord.GuildId != 0 ? _client.GetGuild(_discord.GuildId) : null;

        // Latency is only meaningful once a heartbeat has completed; Discord.Net reports 0 until then,
        // and a 0ms gateway link would read as impossibly good rather than as unmeasured.
        int? latency = _client.ConnectionState == ConnectionState.Connected && _client.Latency > 0
            ? _client.Latency
            : null;

        List<BotChannel> channels = [];
        foreach ((string instance, InstanceSettings settings) in _kgsm.Instances.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(settings.ChannelId))
                continue;

            SocketChannel? channel = ulong.TryParse(settings.ChannelId, out ulong channelId)
                ? _client.GetChannel(channelId)
                : null;
            channels.Add(new BotChannel(
                Instance: instance,
                ChannelId: settings.ChannelId,
                ChannelName: (channel as SocketTextChannel)?.Name,
                Visible: channel is not null));
        }

        return new BotStatus(
            ConnectionState: _client.ConnectionState.ToString(),
            LatencyMs: latency,
            GuildConfigured: _discord.GuildId != 0 ? _discord.GuildId.ToString() : null,
            GuildResolved: guild?.Name,
            GuildMemberCount: guild?.MemberCount,
            CommandCount: CommandManifest.Build(Assembly.GetExecutingAssembly()).Gates.Sum(g => g.Value.Count),
            Channels: channels,
            Announcements: Switches());
    }

    // The announcement switches, read off the bound options rather than re-declared, so this list cannot
    // drift from what the bot actually checks before posting. The keys match the leaf descriptor's, which
    // is what lets the panel put each one next to the control that edits it.
    private List<BotSwitch> Switches()
    {
        AnnouncementOptions a = _discord.Announce;
        return
        [
            new("announceStarted", "Server started", a.Started),
            new("announceReady", "Server ready to play", a.Ready),
            new("announceStopped", "Server stopped", a.Stopped),
            new("announceRestarted", "Server restarted", a.Restarted),
            new("announceCrashed", "Server crashed", a.Crashed),
            new("announceFailed", "Server gave up restarting", a.Failed),
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
