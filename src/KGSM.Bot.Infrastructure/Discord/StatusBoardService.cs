using System.Text;

using Discord;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// Keeps one message per guild current: every server on this host, whether it is up, and how to
/// reach it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the ambient status board, and it is a message.</b> Discord's channel-edit rate limit is
/// aggressive enough that a channel name cannot be kept in step with a server's state — a bot that
/// tries is throttled off the API, losing its announcements too. Editing one message per guild is a
/// different and far more generous bucket, and it carries more than a name ever could.
/// </para>
/// <para>
/// <b>Events mark it dirty; a window decides when to publish.</b> A host reboot is fifteen
/// <c>started</c> events in the same second and must cost one edit, not fifteen. The periodic
/// republish is a backstop for what no event describes — a server stopped outside the engine, a
/// message somebody deleted — and not the mechanism.
/// </para>
/// <para>
/// <b>The message id is stored.</b> Without it a restart posts a second board beside the first and
/// keeps the wrong one current, which is worse than having none: two boards disagreeing is a
/// fabricated status with a timestamp on it.
/// </para>
/// </remarks>
public sealed class StatusBoardService : IStatusBoard, IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly IGuildStore _guilds;
    private readonly IKgsmStateCache _cache;
    private readonly IServerInstanceService _instances;
    private readonly IHostAddressService _addresses;
    private readonly IDiscordSendQueue _queue;
    private readonly DiscordOptions _options;
    private readonly ILogger<StatusBoardService> _logger;

    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _publishing = new(1, 1);

    private Task? _loop;
    private volatile bool _dirty;
    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;

    public StatusBoardService(
        DiscordSocketClient discordClient,
        IGuildStore guilds,
        IKgsmStateCache cache,
        IServerInstanceService instances,
        IHostAddressService addresses,
        IDiscordSendQueue queue,
        IOptions<DiscordOptions> options,
        ILogger<StatusBoardService> logger)
    {
        _discordClient = discordClient;
        _guilds = guilds;
        _cache = cache;
        _instances = instances;
        _addresses = addresses;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Start()
    {
        // The gateway's READY handler fires again on every reconnect, and a second loop would double
        // every edit for the life of the process.
        if (_loop is not null)
            return;

        _dirty = true;
        _loop = Task.Run(() => RunAsync(_stopping.Token));
        _logger.LogInformation(
            "Live status message: publishing at most every {Floor}s, refreshed every {Refresh}s.",
            _options.StatusMessageMinIntervalSeconds, _options.StatusMessageRefreshSeconds);
    }

    /// <inheritdoc />
    public void Invalidate() => _dirty = true;

    /// <inheritdoc />
    public async Task PublishAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (_guilds.Find(guildId) is not GuildTopology topology || !topology.KeepsStatus)
            return;

        Snapshot snapshot = await ReadAsync(cancellationToken);
        await PublishInAsync(topology, snapshot);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Short ticks, so a change is on screen within the floor rather than within a whole period.
        // The tick itself costs nothing: it reads two fields.
        TimeSpan tick = TimeSpan.FromSeconds(2);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tick, cancellationToken);

                TimeSpan since = DateTimeOffset.UtcNow - _lastPublished;
                bool floorPassed = since >= TimeSpan.FromSeconds(_options.StatusMessageMinIntervalSeconds);
                bool refreshDue = since >= TimeSpan.FromSeconds(_options.StatusMessageRefreshSeconds);

                if ((_dirty && floorPassed) || refreshDue)
                    await PublishAllAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A failed publish must not end the loop: the next tick tries again, and the guild
                // that failed is the only one that stayed stale.
                _logger.LogError(e, "The live status message could not be published.");
            }
        }
    }

    private async Task PublishAllAsync(CancellationToken cancellationToken)
    {
        if (!await _publishing.WaitAsync(0, cancellationToken))
            return;

        try
        {
            List<GuildTopology> keeping = [.. _guilds.Configured().Where(g => g.KeepsStatus)];

            // Marked published either way. With nobody keeping a board there is nothing to do, and
            // leaving the flag set would re-read the whole inventory every tick for no reader.
            _dirty = false;
            _lastPublished = DateTimeOffset.UtcNow;

            if (keeping.Count == 0)
                return;

            Snapshot snapshot = await ReadAsync(cancellationToken);

            foreach (GuildTopology topology in keeping)
                await PublishInAsync(topology, snapshot);
        }
        finally
        {
            _publishing.Release();
        }
    }

    // ── reading ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One read of the whole picture, shared by every guild. Reading it per guild would spawn a kgsm
    /// process per server per guild for an answer that cannot differ between them.
    /// </summary>
    private async Task<Snapshot> ReadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, Instance> instances;
        try
        {
            instances = await _cache.GetInstancesAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The instance inventory could not be read for the status message.");
            return new Snapshot([], HostAddresses.Unknown, Readable: false);
        }

        // Each check spawns a kgsm process, so they run together rather than in sequence — the
        // message is as old as the slowest one, not as old as their sum.
        ServerRow[] rows = await Task.WhenAll(instances
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(async pair =>
            {
                Result<bool> active = await _instances.IsActiveAsync(pair.Key);
                return new ServerRow(
                    Name: pair.Key,
                    Blueprint: pair.Value.Blueprint,
                    Ports: pair.Value.Ports ?? [],
                    // Three states, not two: a run state that could not be read is reported as
                    // unread rather than as stopped, which is a different fact about the server.
                    Running: active.IsSuccess ? active.Value : null);
            }));

        HostAddresses addresses = await _addresses.ResolveAsync(cancellationToken);

        return new Snapshot(rows, addresses, Readable: true);
    }

    // ── publishing ────────────────────────────────────────────────────────────────────────────

    private async Task PublishInAsync(GuildTopology topology, Snapshot snapshot)
    {
        try
        {
            if (topology.StatusChannelId is not ulong channelId)
                return;

            if (_discordClient.GetChannel(channelId) is not ITextChannel channel)
            {
                _logger.LogWarning(
                    "Guild {GuildId} keeps a status message in channel {ChannelId}, which cannot be seen.",
                    topology.GuildId, channelId);
                return;
            }

            Embed embed = Render(snapshot);

            // Background lane throughout: the board says what is true now, and a republish that
            // lands a moment later says the same thing. Anything a person is waiting to read goes
            // ahead of it, and the next tick would have refreshed this regardless.
            if (topology.StatusMessageId is ulong messageId)
            {
                // A fetch is a request like any other and spends the same headroom, so it is paced
                // with the rest rather than being made straight off the client. Captured rather than
                // returned, because "no such message" is a successful answer of null and a result
                // type that forbids a null success cannot carry it.
                IMessage? fetched = null;
                Result found = await _queue.SendAsync(
                    $"read the status message in guild {topology.GuildId}",
                    SendLane.Background,
                    // A statement body, deliberately: an expression body would bind the generic
                    // overload, and a message that is genuinely gone is a successful null it cannot
                    // carry.
                    async () => { fetched = await channel.GetMessageAsync(messageId); });

                if (found.IsSuccess && fetched is IUserMessage existing)
                {
                    Result edited = await _queue.SendAsync(
                        $"edit the status message in guild {topology.GuildId}",
                        SendLane.Background,
                        () => existing.ModifyAsync(m => m.Embed = embed));

                    if (edited.IsFailure)
                    {
                        _logger.LogWarning(
                            "The status message in guild {GuildId} could not be edited: {Reason}",
                            topology.GuildId, edited.Error);
                    }

                    return;
                }

                // A fetch that failed is not a message that is gone. Posting a second board on the
                // strength of a request Discord refused is how a guild ends up with two of them
                // disagreeing, and only one being kept current.
                if (found.IsFailure)
                {
                    _logger.LogWarning(
                        "The status message in guild {GuildId} could not be read ({Reason}); " +
                        "leaving it alone rather than posting a second one.",
                        topology.GuildId, found.Error);
                    return;
                }
            }

            // No message recorded, or the one recorded is genuinely gone. Either way this guild needs
            // a new one, and the id is written down before anything else can want it.
            Result<IUserMessage> posted = await _queue.SendAsync(
                $"post a status message in guild {topology.GuildId}",
                SendLane.Background,
                async () => (IUserMessage)await channel.SendMessageAsync(embed: embed));

            if (posted.IsFailure)
            {
                _logger.LogWarning("A status message could not be posted in guild {GuildId}: {Reason}",
                    topology.GuildId, posted.Error);
                return;
            }

            _guilds.SetStatusMessage(topology.GuildId, posted.Value!.Id);

            await TryPinAsync(channel, posted.Value!, topology.GuildId);

            _logger.LogInformation("Posted a new status message in guild {GuildId}.", topology.GuildId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "The status message in guild {GuildId} could not be published.", topology.GuildId);
        }
    }

    /// <summary>
    /// Pins the message, or does not. Pinning needs <c>Manage Messages</c>, and a board that is
    /// merely unpinned still works — so a missing permission costs the pin and nothing else.
    /// </summary>
    private async Task TryPinAsync(ITextChannel channel, IUserMessage message, ulong guildId)
    {
        try
        {
            if (channel is SocketTextChannel socket
                && !socket.Guild.CurrentUser.GetPermissions(socket).ManageMessages)
            {
                _logger.LogInformation(
                    "The status message in guild {GuildId} is not pinned: the bot cannot manage " +
                    "messages there. It is kept current either way.", guildId);
                return;
            }

            Result pinned = await _queue.SendAsync(
                $"pin the status message in guild {guildId}",
                SendLane.Background,
                () => message.PinAsync());

            if (pinned.IsFailure)
            {
                _logger.LogDebug("The status message in guild {GuildId} could not be pinned: {Reason}",
                    guildId, pinned.Error);
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "The status message in guild {GuildId} could not be pinned.", guildId);
        }
    }

    // ── rendering ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole host in one embed.
    /// </summary>
    /// <remarks>
    /// A server whose run state could not be read is marked as unread, never as stopped — the marker
    /// says what is known, and "we could not ask" is not "the answer is no". The address is stated
    /// once for the host rather than repeated per server, because it is the same address.
    /// </remarks>
    private Embed Render(Snapshot snapshot)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Game servers")
            .WithColor(snapshot.Readable ? Color.DarkTeal : Color.LightGrey)
            .WithCurrentTimestamp()
            .WithFooter("Updated");

        if (!snapshot.Readable)
        {
            embed.WithDescription(
                "⚠️ I couldn't read this host's servers just now, so this is not a picture of " +
                "anything. It will fix itself on the next refresh.");
            return embed.Build();
        }

        if (snapshot.Servers.Count == 0)
        {
            embed.WithDescription("No servers are installed on this host.");
            return embed.Build();
        }

        int online = snapshot.Servers.Count(s => s.Running == true);
        embed.WithDescription($"**{online} of {snapshot.Servers.Count}** online.");

        var body = new StringBuilder();
        foreach (ServerRow server in snapshot.Servers)
        {
            body.Append(Marker(server.Running)).Append(" **").Append(server.Name).Append("**");

            if (!string.IsNullOrWhiteSpace(server.Blueprint))
                body.Append(" · ").Append(server.Blueprint);

            if (Join(snapshot.Addresses.Public, server) is string join)
                body.Append(" · `").Append(join).Append('`');

            body.AppendLine();
        }

        // Discord caps an embed field at 1024 characters. A host with more servers than fit gets as
        // many as do plus a line saying so, rather than a silently short list.
        embed.AddField("Servers", Fit(body.ToString(), 1024));

        if (snapshot.Addresses.Source == AddressSource.None)
        {
            embed.AddField("Address",
                "This host could not determine its own external address, so the ports above have " +
                "nothing to be typed after.");
        }

        return embed.Build();
    }

    private string Marker(bool? running) => running switch
    {
        true => Fallback(_options.Status.Online, "🟢"),
        false => Fallback(_options.Status.Offline, "🔴"),
        _ => "❔",
    };

    private static string Fallback(string configured, string literal) =>
        string.IsNullOrWhiteSpace(configured) ? literal : configured;

    /// <summary>
    /// Address and port, or nothing. A server with no declared ports gets no connect string rather
    /// than a bare address with an invented port after it.
    /// </summary>
    private static string? Join(string? address, ServerRow server)
    {
        if (address is null || server.Ports.Count == 0)
            return null;

        return $"{address}:{server.Ports[0].Start}";
    }

    private static string Fit(string text, int limit)
    {
        if (text.Length <= limit)
            return text;

        const string notice = "…and more than fits in one message.";
        int room = limit - notice.Length - 1;
        int cut = text.LastIndexOf('\n', Math.Min(room, text.Length - 1));

        return (cut <= 0 ? text[..room] : text[..cut]) + "\n" + notice;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _publishing.Dispose();
    }

    private sealed record Snapshot(
        IReadOnlyList<ServerRow> Servers, HostAddresses Addresses, bool Readable);

    private sealed record ServerRow(
        string Name, string Blueprint, IReadOnlyList<PortMapping> Ports, bool? Running);
}
