using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Guilds;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The guild store is the only durable thing this bot owns, and the one binding it holds — a server
/// to the channel carrying its history — cannot be re-derived from anywhere if it is lost. These pin
/// what that costs: a binding survives being written and read back by a fresh store, a snowflake
/// beyond what a signed 64-bit integer holds round-trips exactly, and a file a newer build wrote is
/// refused rather than half-read.
/// </summary>
public sealed class SqliteGuildStoreTests : IDisposable
{
    // Past long.MaxValue: a Discord id is unsigned, and the whole reason snowflakes are stored as TEXT
    // is that this value in an INTEGER column comes back as a negative number or not at all.
    private const ulong BigSnowflake = 18_000_000_000_000_000_001;

    private readonly string _directory =
        Directory.CreateTempSubdirectory("kgsm-bot-guild-store-tests").FullName;

    private string DbPath => Path.Combine(_directory, "bot.db");

    private SqliteGuildStore Open() => new(
        Options.Create(new GuildOptions { DbPath = DbPath }),
        NullLogger<SqliteGuildStore>.Instance);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AGuildIsConfiguredByHavingAnAnnouncementChannel()
    {
        SqliteGuildStore store = Open();

        store.Configured().Should().BeEmpty("nothing is announced until an admin runs /setup");
        store.Find(1).Should().BeNull();

        store.SetAnnounceChannel(1, 2, "heisen").IsSuccess.Should().BeTrue();

        GuildTopology topology = store.Find(1)!;
        topology.AnnounceChannelId.Should().Be(2ul);
        topology.ConfiguredBy.Should().Be("heisen");
        topology.HasBoard.Should().BeFalse("the board is the thing you deliberately turn on");
    }

    /// <summary>
    /// The board is on because there is a category, not because a flag says so beside one — so turning
    /// it off is clearing the category, and there is no second value that can disagree with it.
    /// </summary>
    [Fact]
    public void TheBoardIsTheCategoryAndTurningItOffClearsIt()
    {
        SqliteGuildStore store = Open();
        store.SetAnnounceChannel(1, 2, "heisen");

        store.SetBoard(1, 99).IsSuccess.Should().BeTrue();
        store.Find(1)!.BoardCategoryId.Should().Be(99ul);
        store.Find(1)!.HasBoard.Should().BeTrue();

        store.SetBoard(1, null).IsSuccess.Should().BeTrue();
        store.Find(1)!.BoardCategoryId.Should().BeNull();
        store.Find(1)!.HasBoard.Should().BeFalse();
    }

    /// <summary>
    /// A board on a guild with nowhere to fall back to would silently drop every server that has no
    /// channel of its own, so the announcement channel is required first and this refuses without it.
    /// </summary>
    [Fact]
    public void TheBoardCannotBeTurnedOnForAGuildThatIsNotSetUp()
    {
        SqliteGuildStore store = Open();

        Result result = store.SetBoard(404, 99);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("/setup announce");
        store.Find(404).Should().BeNull();
    }

    /// <summary>
    /// The defect the store exists to close: a channel the bot made on install used to live in the
    /// in-memory options map, so the next restart forgot it and the server was given a new channel
    /// beside the one holding its history.
    /// </summary>
    [Fact]
    public void ABindingSurvivesTheProcessThatWroteIt()
    {
        SqliteGuildStore writer = Open();
        writer.SetAnnounceChannel(1, 2, "heisen");
        writer.BindChannel(1, "minecraft-homestead", BigSnowflake).IsSuccess.Should().BeTrue();

        // A second store over the same file is what a restart looks like from the data's side.
        SqliteGuildStore reader = Open();

        reader.ChannelFor(1, "minecraft-homestead").Should().Be(BigSnowflake,
            "a snowflake past long.MaxValue is exactly what an INTEGER column would get wrong");
        reader.ChannelsIn(1).Should().ContainSingle()
            .Which.Instance.Should().Be("minecraft-homestead",
                "a hyphen in an instance name is the case the settings file could never express");
    }

    [Fact]
    public void ABindingCanBeRepointedAndDropped()
    {
        SqliteGuildStore store = Open();
        store.SetAnnounceChannel(1, 2, "heisen");

        store.BindChannel(1, "factorio", 10);
        store.BindChannel(1, "factorio", 11);
        store.ChannelFor(1, "factorio").Should().Be(11ul);
        store.ChannelsIn(1).Should().ContainSingle("re-binding replaces, it does not accumulate");

        store.UnbindChannel(1, "factorio").IsSuccess.Should().BeTrue();
        store.ChannelFor(1, "factorio").Should().BeNull();
    }

    /// <summary>
    /// Two guilds are two independent configurations: one running a board and one taking everything in
    /// a single channel is the shape the ecosystem expects, and neither may see the other's bindings.
    /// </summary>
    [Fact]
    public void GuildsDoNotShareBindings()
    {
        SqliteGuildStore store = Open();
        store.SetAnnounceChannel(1, 10, "heisen");
        store.SetBoard(1, 99);
        store.SetAnnounceChannel(2, 20, "someone-else");

        store.BindChannel(1, "factorio", 111);

        store.ChannelFor(2, "factorio").Should().BeNull();
        store.ChannelsIn(2).Should().BeEmpty();
        store.Configured().Select(g => g.GuildId).Should().Equal(1ul, 2ul);
    }

    /// <summary>
    /// Forgetting a guild takes its bindings with it — leaving them would have the next guild set up
    /// under the same id inherit channels nobody pointed it at.
    /// </summary>
    [Fact]
    public void ForgettingAGuildTakesItsBindings()
    {
        SqliteGuildStore store = Open();
        store.SetAnnounceChannel(1, 10, "heisen");
        store.BindChannel(1, "factorio", 111);

        store.Forget(1).IsSuccess.Should().BeTrue();

        store.Find(1).Should().BeNull();
        store.ChannelsIn(1).Should().BeEmpty();
        store.Configured().Should().BeEmpty();
    }

    /// <summary>
    /// A file written by a newer kgsm-bot is refused rather than read at whatever this build happens
    /// to understand — and refused into an unavailable store, not out of the constructor, so the rest
    /// of the bot keeps working and says why nothing is being announced.
    /// </summary>
    [Fact]
    public void AStoreFromANewerBuildIsRefusedWithoutTakingTheBotDown()
    {
        Open().SetAnnounceChannel(1, 2, "heisen");

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE schema_version SET version = {SqliteGuildStore.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }

        SqliteGuildStore store = Open();

        store.Available.Should().BeFalse();
        store.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        store.Configured().Should().BeEmpty("an unreadable store announces nowhere");
        store.SetAnnounceChannel(1, 2, "heisen").IsFailure.Should().BeTrue(
            "/setup must refuse rather than pretend it recorded something");
    }
}
