using System.Data;
using System.Globalization;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Guilds;

/// <summary>
/// The bot's own SQLite file, holding which Discord servers this host announces into and which
/// channel each game server reports in.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the bot's file, not the account store.</b> Guild topology is not authority and does not
/// belong beside credentials, and this process is its only writer.
/// </para>
/// <para>
/// <b>It lives outside the install prefix.</b> <c>deploy/deploy.sh</c> syncs the prefix with
/// <c>rsync -a --delete</c>, so anything the bot wrote in there would be deleted by the next deploy.
/// </para>
/// <para>
/// <b>Additive-only, with a version floor.</b> Wiping this file loses every channel binding, and a
/// binding is the only thing tying a server to the channel holding its history. Add tables, add
/// nullable columns; never drop, rename or repurpose. A file written by a newer build is refused
/// rather than half-read.
/// </para>
/// <para>
/// <b>Snowflakes are stored as TEXT.</b> A 64-bit unsigned Discord id in a signed
/// <c>INTEGER</c> column is a parse waiting to be got wrong.
/// </para>
/// <para>
/// Calls are synchronous: point queries against a local file, made at Discord's cadence — one per
/// announcement, a handful per <c>/setup</c>. A connection is opened per operation and returned to
/// the pool rather than held, so nothing pins a WAL read snapshot.
/// </para>
/// </remarks>
public sealed class SqliteGuildStore : IGuildStore
{
    /// <summary>The schema this build writes and is willing to read.</summary>
    public const int SchemaVersion = 2;

    private const string GuildColumns =
        "guild_id, announce_channel_id, board_category_id, configured_by, configured_utc, updated_utc, " +
        "status_channel_id, status_message_id";

    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _connectionString;
    private readonly ILogger<SqliteGuildStore> _logger;

    public SqliteGuildStore(IOptions<GuildOptions> options, ILogger<SqliteGuildStore> logger)
    {
        _logger = logger;
        string path = options.Value.DbPath;

        try
        {
            string full = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = (int)Math.Ceiling(BusyTimeout.TotalSeconds),
            }.ToString();

            Initialize(full);
            logger.LogInformation("Guild store opened at {Path}.", full);
        }
        catch (Exception e)
        {
            _connectionString = null;
            UnavailableReason = $"The guild store at '{path}' could not be opened: {e.Message}";
            logger.LogError(e,
                "Guild store at {Path} could not be opened. Nothing will be announced anywhere and " +
                "/setup will refuse; slash commands and the journal reader are unaffected.", path);
        }
    }

    /// <inheritdoc />
    public bool Available => _connectionString is not null;

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    // ── schema ────────────────────────────────────────────────────────────────────────────────

    private void Initialize(string path)
    {
        using SqliteConnection connection = Connect();

        // Ahead of WAL: SQLite stamps -wal/-shm with the database's mode as it creates them, so a
        // chmod afterwards would leave two files carrying the same pages at the wider mode.
        Restrict(path);

        Execute(connection, "PRAGMA journal_mode=WAL;");

        using SqliteTransaction transaction =
            connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS guilds (
                guild_id            TEXT PRIMARY KEY,
                announce_channel_id TEXT NOT NULL,
                board_category_id   TEXT NULL,
                configured_by       TEXT NOT NULL,
                configured_utc      TEXT NOT NULL,
                updated_utc         TEXT NOT NULL,
                status_channel_id   TEXT NULL,
                status_message_id   TEXT NULL);

            CREATE TABLE IF NOT EXISTS guild_channels (
                guild_id    TEXT NOT NULL,
                instance    TEXT NOT NULL,
                channel_id  TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY (guild_id, instance));
            """, transaction);

        object? found = Scalar(connection, "SELECT version FROM schema_version LIMIT 1;", transaction);

        if (found is null or DBNull)
        {
            Execute(connection, "INSERT INTO schema_version (version) VALUES ($v);", transaction,
                ("$v", SchemaVersion));
        }
        else
        {
            long version = Convert.ToInt64(found, CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The guild store at '{path}' is at schema version {version}; this build " +
                    $"understands {SchemaVersion}. It was written by a newer kgsm-bot. Refusing to " +
                    "open it rather than reading a file this build only half understands.");
            }

            // Forward steps, additive only. A file written by version 1 has the guilds table without
            // the status columns; `CREATE TABLE IF NOT EXISTS` above did nothing to it, so the
            // columns are added here. Losing this file loses every channel binding, which is why it
            // is migrated in place and never recreated.
            if (version < 2)
            {
                Execute(connection, """
                    ALTER TABLE guilds ADD COLUMN status_channel_id TEXT NULL;
                    ALTER TABLE guilds ADD COLUMN status_message_id TEXT NULL;
                    """, transaction);
            }

            if (version < SchemaVersion)
            {
                Execute(connection, "UPDATE schema_version SET version = $v;", transaction,
                    ("$v", SchemaVersion));

                _logger.LogInformation(
                    "Guild store migrated from schema version {From} to {To}.", version, SchemaVersion);
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// 0600 the database file. It is not a secret, but it decides where this host broadcasts, and a
    /// file another account can rewrite is a file another account can point at their own channel.
    /// </summary>
    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ── guilds ────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<GuildTopology> Configured()
    {
        if (_connectionString is null)
            return [];

        try
        {
            using SqliteConnection connection = Connect();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {GuildColumns} FROM guilds ORDER BY guild_id;";

            List<GuildTopology> guilds = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                guilds.Add(ReadGuild(reader));

            return guilds;
        }
        catch (Exception e)
        {
            // Reading nothing is the same shape as "no guild is configured": the bot posts nowhere
            // and says why in the log, rather than crashing the journal reader that called it.
            _logger.LogError(e, "Could not read the configured guilds — nothing will be announced.");
            return [];
        }
    }

    /// <inheritdoc />
    public GuildTopology? Find(ulong guildId)
    {
        if (_connectionString is null)
            return null;

        try
        {
            using SqliteConnection connection = Connect();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {GuildColumns} FROM guilds WHERE guild_id = $g;";
            command.Parameters.AddWithValue("$g", Id(guildId));

            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadGuild(reader) : null;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not read guild {GuildId} from the store.", guildId);
            return null;
        }
    }

    /// <inheritdoc />
    public Result SetAnnounceChannel(ulong guildId, ulong announceChannelId, string configuredBy)
        => Write($"configure guild {guildId}", connection =>
        {
            string now = Now();
            Execute(connection, $"""
                INSERT INTO guilds ({GuildColumns})
                VALUES ($g, $c, NULL, $by, $now, $now, NULL, NULL)
                ON CONFLICT(guild_id) DO UPDATE SET
                    announce_channel_id = excluded.announce_channel_id,
                    configured_by       = excluded.configured_by,
                    updated_utc         = excluded.updated_utc;
                """,
                transaction: null,
                ("$g", Id(guildId)), ("$c", Id(announceChannelId)), ("$by", configuredBy), ("$now", now));
        });

    /// <inheritdoc />
    public Result SetBoard(ulong guildId, ulong? boardCategoryId)
        => Write($"set the board for guild {guildId}", connection =>
        {
            int changed = Execute(connection, """
                UPDATE guilds SET board_category_id = $cat, updated_utc = $now WHERE guild_id = $g;
                """,
                transaction: null,
                ("$g", Id(guildId)),
                ("$cat", boardCategoryId is ulong c ? Id(c) : DBNull.Value),
                ("$now", Now()));

            if (changed == 0)
            {
                throw new InvalidOperationException(
                    "this guild has no announcement channel yet — run /setup announce first");
            }
        });

    /// <inheritdoc />
    public Result SetStatusChannel(ulong guildId, ulong? statusChannelId)
        => Write($"set the status message channel for guild {guildId}", connection =>
        {
            // The message id goes with the channel. A message id kept across a move would name a
            // message in the channel it was moved away from, and the next refresh would edit that
            // one — leaving a stale board somewhere nobody is looking.
            int changed = Execute(connection, """
                UPDATE guilds
                SET status_channel_id = $ch, status_message_id = NULL, updated_utc = $now
                WHERE guild_id = $g;
                """,
                transaction: null,
                ("$g", Id(guildId)),
                ("$ch", statusChannelId is ulong c ? Id(c) : DBNull.Value),
                ("$now", Now()));

            if (changed == 0)
            {
                throw new InvalidOperationException(
                    "this guild has no announcement channel yet — run /setup announce first");
            }
        });

    /// <inheritdoc />
    public Result SetStatusMessage(ulong guildId, ulong statusMessageId)
        => Write($"record the status message for guild {guildId}", connection =>
            Execute(connection, """
                UPDATE guilds SET status_message_id = $m WHERE guild_id = $g;
                """,
                transaction: null,
                ("$g", Id(guildId)), ("$m", Id(statusMessageId))));

    /// <inheritdoc />
    public Result Forget(ulong guildId)
        => Write($"forget guild {guildId}", connection =>
        {
            using SqliteTransaction transaction =
                connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

            Execute(connection, "DELETE FROM guild_channels WHERE guild_id = $g;", transaction,
                ("$g", Id(guildId)));
            Execute(connection, "DELETE FROM guilds WHERE guild_id = $g;", transaction,
                ("$g", Id(guildId)));

            transaction.Commit();
        });

    // ── channel bindings ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public ulong? ChannelFor(ulong guildId, string instance)
    {
        if (_connectionString is null)
            return null;

        try
        {
            using SqliteConnection connection = Connect();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT channel_id FROM guild_channels WHERE guild_id = $g AND instance = $i;";
            command.Parameters.AddWithValue("$g", Id(guildId));
            command.Parameters.AddWithValue("$i", instance);

            return command.ExecuteScalar() is string id && ulong.TryParse(id, out ulong channelId)
                ? channelId
                : null;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not read {Instance}'s channel in guild {GuildId}.", instance, guildId);
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GuildChannel> ChannelsIn(ulong guildId)
    {
        if (_connectionString is null)
            return [];

        try
        {
            using SqliteConnection connection = Connect();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT guild_id, instance, channel_id, created_utc
                FROM guild_channels WHERE guild_id = $g ORDER BY instance;
                """;
            command.Parameters.AddWithValue("$g", Id(guildId));

            List<GuildChannel> channels = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                channels.Add(new GuildChannel(
                    GuildId: ParseId(reader.GetString(0)),
                    Instance: reader.GetString(1),
                    ChannelId: ParseId(reader.GetString(2)),
                    CreatedUtc: ParseTime(reader.GetString(3))));
            }

            return channels;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not read the channel bindings for guild {GuildId}.", guildId);
            return [];
        }
    }

    /// <inheritdoc />
    public Result BindChannel(ulong guildId, string instance, ulong channelId)
        => Write($"bind {instance} to a channel in guild {guildId}", connection =>
            Execute(connection, """
                INSERT INTO guild_channels (guild_id, instance, channel_id, created_utc)
                VALUES ($g, $i, $c, $now)
                ON CONFLICT(guild_id, instance) DO UPDATE SET channel_id = excluded.channel_id;
                """,
                transaction: null,
                ("$g", Id(guildId)), ("$i", instance), ("$c", Id(channelId)), ("$now", Now())));

    /// <inheritdoc />
    public Result UnbindChannel(ulong guildId, string instance)
        => Write($"unbind {instance} in guild {guildId}", connection =>
            Execute(connection, "DELETE FROM guild_channels WHERE guild_id = $g AND instance = $i;",
                transaction: null,
                ("$g", Id(guildId)), ("$i", instance)));

    // ── plumbing ──────────────────────────────────────────────────────────────────────────────

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, $"PRAGMA busy_timeout={(int)BusyTimeout.TotalMilliseconds};");
        return connection;
    }

    /// <summary>
    /// A write, with the one failure shape every caller has to handle: it did not happen, and here is
    /// what to tell the person who asked. An unopenable store refuses here rather than throwing, so
    /// <c>/setup</c> can say so instead of failing the interaction.
    /// </summary>
    private Result Write(string what, Action<SqliteConnection> write)
    {
        if (_connectionString is null)
            return Result.Failure(UnavailableReason ?? "the guild store is unavailable");

        try
        {
            using SqliteConnection connection = Connect();
            write(connection);
            return Result.Success();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not {What}.", what);
            return Result.Failure(e.Message);
        }
    }

    private static GuildTopology ReadGuild(SqliteDataReader reader) => new(
        GuildId: ParseId(reader.GetString(0)),
        AnnounceChannelId: ParseId(reader.GetString(1)),
        BoardCategoryId: reader.IsDBNull(2) ? null : ParseId(reader.GetString(2)),
        ConfiguredBy: reader.GetString(3),
        ConfiguredUtc: ParseTime(reader.GetString(4)),
        UpdatedUtc: ParseTime(reader.GetString(5)),
        StatusChannelId: reader.IsDBNull(6) ? null : ParseId(reader.GetString(6)),
        StatusMessageId: reader.IsDBNull(7) ? null : ParseId(reader.GetString(7)));

    private static string Id(ulong snowflake) => snowflake.ToString(CultureInfo.InvariantCulture);

    private static ulong ParseId(string stored) => ulong.Parse(stored, CultureInfo.InvariantCulture);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string stored) =>
        DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static int Execute(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteScalar();
    }
}
