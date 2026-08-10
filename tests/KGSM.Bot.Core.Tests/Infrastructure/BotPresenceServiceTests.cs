using Discord.WebSocket;

using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The line beside the bot's own name. It is the shortest thing this bot says and the most widely
/// seen — every guild, with no channel and no setup — so it gets the same rule as everything longer:
/// a number appears only when it was measured, and an incomplete total is marked as a floor.
/// </summary>
public sealed class BotPresenceServiceTests
{
    private readonly IKgsmStateCache _cache = Substitute.For<IKgsmStateCache>();
    private readonly IPlayerRoster _roster = Substitute.For<IPlayerRoster>();

    private BotPresenceService Presence() => new(
        new DiscordSocketClient(),
        _cache,
        _roster,
        Options.Create(new DiscordOptions()),
        NullLogger<BotPresenceService>.Instance);

    private void Inventory(params string[] names) =>
        _cache.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(names.ToDictionary(n => n, n => new Instance { Name = n })
                as IReadOnlyDictionary<string, Instance>);

    private void InventoryUnreadable() =>
        _cache.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, Instance>>(_ => throw new InvalidOperationException("kgsm is not there"));

    private void Rosters(params ServerRoster[] rosters) =>
        _roster.GetAllAsync(Arg.Any<CancellationToken>()).Returns(rosters);

    private static ServerRoster Playing(string name, params string[] players) =>
        new(name, RosterKnowledge.Known, [.. players.Select(p => new RosterPlayer(p, null))], Running: true);

    private static ServerRoster Stopped(string name) => new(name, RosterKnowledge.Stopped, [], Running: false);

    private static ServerRoster Unseeable(string name) =>
        new(name, RosterKnowledge.NotObservable, [], Running: true);

    private static ServerRoster RunStateUnread(string name) =>
        new(name, RosterKnowledge.Unavailable, [], Running: null);

    // ── the wording ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ItCountsServersAndHowManyAreUp()
    {
        BotPresenceService.Describe([Playing("a"), Playing("b"), Stopped("c")])
            .Should().Be("3 servers · 2 online");
    }

    [Fact]
    public void OneServerIsNotPluralised()
    {
        BotPresenceService.Describe([Stopped("only")]).Should().Be("1 server · 0 online");
    }

    [Fact]
    public void PlayersAreAddedOnlyWhenSomebodyIsPlaying()
    {
        BotPresenceService.Describe([Playing("a", "alice", "bob"), Stopped("b")])
            .Should().Be("2 servers · 1 online · 2 playing");
    }

    /// <summary>
    /// An empty host is not "0 playing": on a host whose games report nobody that would be a lie, and
    /// on a quiet one it is noise in the two words a member list shows.
    /// </summary>
    [Fact]
    public void NobodyPlayingIsNotSaid()
    {
        BotPresenceService.Describe([Playing("a"), Playing("b")])
            .Should().Be("2 servers · 2 online");
    }

    [Fact]
    public void AnUnreadRunStateMakesTheOnlineCountAFloor()
    {
        BotPresenceService.Describe([Playing("a"), RunStateUnread("b")])
            .Should().Be("2 servers · 1+ online");
    }

    /// <summary>
    /// A game that reports no players is running, counted as online, and cannot contribute to a total
    /// — so the total is a floor. The server may be full.
    /// </summary>
    [Fact]
    public void AServerNobodyCanSeeIntoMakesThePlayerCountAFloor()
    {
        BotPresenceService.Describe([Playing("a", "alice"), Unseeable("b")])
            .Should().Be("2 servers · 2 online · 1+ playing");
    }

    /// <summary>
    /// A stopped server has nobody on it, so it takes nothing away from the total's completeness. Only
    /// a <i>running</i> server whose roster is unknown does.
    /// </summary>
    [Fact]
    public void AStoppedServerDoesNotMakeThePlayerCountAFloor()
    {
        BotPresenceService.Describe([Playing("a", "alice"), Stopped("b")])
            .Should().Be("2 servers · 1 online · 1 playing");
    }

    // ── what it says when it cannot say anything ──────────────────────────────────────────────

    [Fact]
    public async Task AHostThatCannotBeReadSaysSoRatherThanCountingZero()
    {
        InventoryUnreadable();

        (await Presence().ComposeAsync()).Should().Be(BotPresenceService.Unreadable);
    }

    /// <summary>
    /// The reason the inventory is read separately from the roster: an empty roster means both of
    /// these, and they are opposite things to put in front of somebody.
    /// </summary>
    [Fact]
    public async Task AHostWithNoServersIsNotAHostThatCouldNotBeRead()
    {
        Inventory();

        (await Presence().ComposeAsync()).Should().Be("a host with no servers");
    }

    [Fact]
    public async Task ServersWithNoRosterAtAllIsReportedAsUnreadable()
    {
        Inventory("minecraft");
        Rosters();

        (await Presence().ComposeAsync()).Should().Be(BotPresenceService.Unreadable);
    }

    [Fact]
    public async Task AReadableHostIsDescribed()
    {
        Inventory("minecraft", "terraria");
        Rosters(Playing("minecraft", "alice"), Stopped("terraria"));

        (await Presence().ComposeAsync()).Should().Be("2 servers · 1 online · 1 playing");
    }
}
