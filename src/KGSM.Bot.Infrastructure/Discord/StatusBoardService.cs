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
    private readonly IHostAddressService _addresses;
    private readonly IPlayerRoster _roster;
    private readonly IBackupInsight _backups;
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
        IHostAddressService addresses,
        IPlayerRoster roster,
        IBackupInsight backups,
        IDiscordSendQueue queue,
        IOptions<DiscordOptions> options,
        ILogger<StatusBoardService> logger)
    {
        _discordClient = discordClient;
        _guilds = guilds;
        _cache = cache;
        _addresses = addresses;
        _roster = roster;
        _backups = backups;
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

        // Run state and who is playing, read once for the whole board through the same service
        // /players uses — there is exactly one place either number comes from, so the board and the
        // command cannot print different things about the same moment. It reads each server's run
        // state to decide what its roster means, and hands that back, so the board joins to it rather
        // than spawning a second kgsm process per server for a fact already in hand.
        Dictionary<string, ServerRoster> rosters;
        try
        {
            rosters = (await _roster.GetAllAsync(cancellationToken))
                .ToDictionary(r => r.Server, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            // Every row falls back to unread rather than to stopped: a board that cannot see the host
            // says so, and saying "offline" on the strength of a failed read is the fabrication.
            _logger.LogWarning(e, "Run state and player counts could not be read for the status message.");
            rosters = [];
        }

        // Cached per server and dropped on the engine's own backup events, so the whole-host read is
        // one process per server on a change rather than one per publish. What is cached is the
        // engine's timestamp; the age below is worked out now, so a stale entry still reads correctly.
        IReadOnlyDictionary<string, InstanceBackup?> backups;
        try
        {
            backups = await _backups.LatestAsync(cancellationToken);
        }
        catch (Exception e)
        {
            // The board is a picture of run state first; losing this costs the backup markers only.
            _logger.LogWarning(e, "Backup ages could not be read for the status message.");
            backups = new Dictionary<string, InstanceBackup?>();
        }

        ServerRow[] rows = [.. instances
            // Ordered by what the row is read as, so the list is in the order somebody scans it.
            .OrderBy(pair => pair.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                rosters.TryGetValue(pair.Key, out ServerRoster? roster);
                return new ServerRow(
                    Name: pair.Key,
                    DisplayName: pair.Value.DisplayName,
                    Blueprint: pair.Value.Blueprint,
                    Ports: pair.Value.Ports ?? [],
                    // Three states, not two: a run state that could not be read is reported as
                    // unread rather than as stopped, which is a different fact about the server.
                    Running: roster?.Running,
                    // Null wherever the count is not knowable, which the renderer prints as nothing
                    // at all rather than as a zero.
                    Players: roster?.Count,
                    // Three states again, and the same discipline: an absent key is a server whose
                    // backups could not be read, which is not the same as one that has none.
                    Backup: backups.TryGetValue(pair.Key, out InstanceBackup? latest)
                        ? new BackupAge(latest?.CreatedAt, latest is not null)
                        : null,
                    // Read off the same roster the run state came from, so the row cannot say the
                    // library is reachable and the run state is unknown about two different moments.
                    LibraryAway: roster?.LibraryAway ?? false,
                    Library: roster?.Library ?? string.Empty);
            })];

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

            // Read once for the host, narrowed here: the picture is the same everywhere, but which
            // part of it a guild is shown is that guild's own setting. A board still listing a server
            // the guild unfollowed would contradict the filter sitting next to it in the same channel.
            Embed embed = Render(snapshot.For(_guilds.FollowedServers(topology.GuildId)));

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
            // Two different empties, and telling a guild the host is empty when it is really their
            // own filter would send somebody looking for a fault that is not there.
            embed.WithDescription(snapshot.Narrowed
                ? "None of the servers this Discord server follows are installed here. " +
                  "`/setup show` lists them."
                : "No servers are installed on this host.");
            return embed.Build();
        }

        int online = snapshot.Servers.Count(s => s.Running == true);
        embed.WithDescription($"**{online} of {snapshot.Servers.Count}** online.{Playing(snapshot)}");

        var body = new StringBuilder();
        foreach (ServerRow server in snapshot.Servers)
        {
            body.Append(Marker(server.Running)).Append(" **").Append(server.DisplayName).Append("**");

            // The id, and only when it is not already the line's first word: it is what a command
            // takes, so somebody reading the board has the string they need to type. A server that
            // was never labelled would otherwise be printed twice.
            if (!string.Equals(server.DisplayName, server.Name, StringComparison.Ordinal))
                body.Append(" `").Append(server.Name).Append('`');

            if (!string.IsNullOrWhiteSpace(server.Blueprint))
                body.Append(" · ").Append(server.Blueprint);

            // Said before the counts, because it is the reason they are missing.
            if (server.LibraryAway)
            {
                body.Append(" · 📦 library away");
                if (!string.IsNullOrWhiteSpace(server.Library))
                    body.Append(" (`").Append(server.Library).Append("`)");
            }

            // Only where the count is a measurement, and only when somebody is on — an empty server
            // does not need saying twice, and a server nobody can see into must not be given a zero.
            if (server.Players is > 0)
                body.Append(" · 👥 ").Append(server.Players);

            if (Backup(server.Backup) is string backup)
                body.Append(" · ").Append(backup);

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

    /// <summary>
    /// The host's player total, or nothing at all.
    /// </summary>
    /// <remarks>
    /// Said only when somebody is actually playing: a board that reads "0 playing" on a quiet host is
    /// noise, and on a host whose games report no players it would be a lie. A total is also only ever
    /// a sum of the servers that could be counted, so it is phrased as a floor rather than a fact when
    /// any of them could not.
    /// </remarks>
    private static string Playing(Snapshot snapshot)
    {
        int total = snapshot.Servers.Sum(s => s.Players ?? 0);
        if (total == 0)
            return string.Empty;

        bool complete = snapshot.Servers.All(s => s.Players is not null || s.Running != true);
        return complete ? $" **{total}** playing." : $" **{total}+** playing.";
    }

    /// <summary>
    /// The backup marker, or nothing at all — which is the usual answer.
    /// </summary>
    /// <remarks>
    /// <b>Silence means "recent enough", and that is what makes this readable.</b> An age printed
    /// beside every server buries the one that matters among the fifteen that do not, so this speaks
    /// only past <c>BackupStaleAfterHours</c> and for a server that has never been backed up. A server
    /// whose backups could not be read gets nothing rather than a warning — not looking is not the
    /// same as looking and finding nothing, and only one of those is the server's problem.
    /// </remarks>
    private string? Backup(BackupAge? backup)
    {
        if (backup is null)
            return null;

        if (!backup.Exists)
            return "💾 never";

        if (backup.TakenAt is not DateTimeOffset taken)
            return null;

        TimeSpan age = DateTimeOffset.UtcNow - taken;
        if (age < TimeSpan.FromHours(_options.BackupStaleAfterHours))
            return null;

        return age.TotalDays >= 1
            ? $"💾 {age.TotalDays:0}d"
            : $"💾 {age.TotalHours:0}h";
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
        IReadOnlyList<ServerRow> Servers, HostAddresses Addresses, bool Readable)
    {
        /// <summary>
        /// Whether this host has servers that this view is not showing — the difference between a
        /// guild following none of what is installed and a host with nothing installed at all.
        /// </summary>
        public bool Narrowed { get; private init; }

        /// <summary>
        /// The same picture, narrowed to the servers a guild follows. An empty <paramref name="follows"/>
        /// is no filter and returns the whole thing, which is what a guild that has never set one has.
        /// </summary>
        public Snapshot For(IReadOnlyList<string> follows)
        {
            if (follows.Count == 0)
                return this;

            var wanted = follows.ToHashSet(StringComparer.OrdinalIgnoreCase);

            return this with
            {
                Servers = [.. Servers.Where(s => wanted.Contains(s.Name))],
                Narrowed = true,
            };
        }
    }

    /// <summary>
    /// One row of the board. <c>Players</c> is null wherever the count is not knowable — a game that
    /// reports no players, a supervisor that could not be asked, a server that is not running — and
    /// null prints as nothing, because printing it as 0 would claim an empty server.
    /// </summary>
    /// <summary>
    /// One line of the board.
    /// </summary>
    /// <remarks>
    /// <c>LibraryAway</c> says the server's files are out of reach, which is why its row can be
    /// marked unread rather than stopped. It earns a word of its own on the line: a bare "unknown"
    /// sends somebody looking at the server, and "its library is away" sends them at the disk.
    /// </remarks>
    /// <remarks>
    /// <c>Name</c> is the id — what the filter, the bindings and every command key on — and
    /// <c>DisplayName</c> is what a person calls it. Both are printed: the label is what somebody
    /// reads the board for, and the id is what they type afterwards.
    /// </remarks>
    private sealed record ServerRow(
        string Name, string DisplayName, string Blueprint, IReadOnlyList<PortMapping> Ports,
        bool? Running, int? Players, BackupAge? Backup, bool LibraryAway = false, string Library = "");

    /// <summary>
    /// What is known about a server's newest backup. Null in <c>ServerRow</c> means the backups could
    /// not be read at all — distinct from <see cref="Exists"/> being false, which is a server that was
    /// read and genuinely has none.
    /// </summary>
    /// <param name="TakenAt">
    /// When the newest one was captured, or null — either because there is none, or because the
    /// manifest recorded no time. Both mean no age can be stated.
    /// </param>
    /// <param name="Exists">Whether there is a backup at all.</param>
    private sealed record BackupAge(DateTimeOffset? TakenAt, bool Exists);
}
