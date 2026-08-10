using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The join behind "who is playing": run state from the engine, observability and sessions from the
/// supervisor.
/// </summary>
/// <remarks>
/// Every test here is really the same test — that a number is only ever reported when it was
/// measured. The four <see cref="RosterKnowledge"/> states exist so the three ways of not knowing
/// cannot collapse into a zero, and these pin each of them at the seam rather than in the wording.
/// </remarks>
public sealed class PlayerRosterTests
{
    private readonly IKgsmStateCache _cache = Substitute.For<IKgsmStateCache>();
    private readonly IServerInstanceService _instances = Substitute.For<IServerInstanceService>();
    private readonly IWatchdogClient _watchdog = Substitute.For<IWatchdogClient>();

    private PlayerRoster Roster() => new(_cache, _instances, _watchdog, NullLogger<PlayerRoster>.Instance);

    private void Inventory(params string[] names) =>
        _cache.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(names.ToDictionary(n => n, n => new Instance { Name = n })
                as IReadOnlyDictionary<string, Instance>);

    private void Running(string name, bool running) =>
        _instances.IsActiveAsync(name).Returns(Result.Success(running));

    private void RunStateUnreadable(string name) =>
        _instances.IsActiveAsync(name).Returns(Result.Failure<bool>("the engine could not be asked"));

    private void Presence(params (string Name, string Detection, string[] Players)[] entries) =>
        _watchdog.GetPlayerPresenceAsync(Arg.Any<CancellationToken>())
            .Returns(entries.ToDictionary(
                e => e.Name,
                e => new WatchdogInstancePresence
                {
                    Detection = e.Detection,
                    Players = [.. e.Players.Select(p => new WatchdogPlayer { Name = p })],
                }) as IReadOnlyDictionary<string, WatchdogInstancePresence>);

    private void SupervisorDown() =>
        _watchdog.GetPlayerPresenceAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, WatchdogInstancePresence>?)null);

    // ── the one measured case ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnObservedServerReportsWhoIsOn()
    {
        Inventory("minecraft");
        Running("minecraft", true);
        Presence(("minecraft", "log", ["Flysenberg", "alice"]));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Known);
        roster.Count.Should().Be(2);
        roster.Players.Select(p => p.Label).Should().Equal("alice", "Flysenberg"); // name-ordered
    }

    /// <summary>
    /// The genuine zero, and the only one. Detection is working and it sees nobody.
    /// </summary>
    [Fact]
    public async Task AnObservedServerWithNobodyOnItCountsZero()
    {
        Inventory("minecraft");
        Running("minecraft", true);
        Presence(("minecraft", "log", []));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Known);
        roster.Count.Should().Be(0);
    }

    // ── the three ways of not knowing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The headline rule. A game that reports nothing may be full, and a count of zero would say it
    /// is empty.
    /// </summary>
    [Fact]
    public async Task AGameThatReportsNoPlayersHasNoCount()
    {
        Inventory("starbound");
        Running("starbound", true);
        Presence(("starbound", "none", []));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.NotObservable);
        roster.Count.Should().BeNull();
    }

    [Fact]
    public async Task AnUnreachableSupervisorHasNoCount()
    {
        Inventory("minecraft");
        Running("minecraft", true);
        SupervisorDown();

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Unavailable);
        roster.Count.Should().BeNull();
    }

    /// <summary>
    /// The supervisor answered but said nothing about this server — installed since it last read the
    /// inventory. Silence about a server is not a statement that nobody is on it.
    /// </summary>
    [Fact]
    public async Task AServerTheSupervisorDidNotMentionHasNoCount()
    {
        Inventory("brand-new");
        Running("brand-new", true);
        Presence(("minecraft", "log", []));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Unavailable);
        roster.Count.Should().BeNull();
    }

    // ── the run-state join ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stopped server has nobody on it, whatever a stale session map still holds — a process that is
    /// not running has no connections. Reported as stopped rather than as zero, which is the sentence
    /// a person actually wants.
    /// </summary>
    [Fact]
    public async Task AStoppedServerIsStoppedEvenWhenSessionsLinger()
    {
        Inventory("minecraft");
        Running("minecraft", false);
        Presence(("minecraft", "log", ["a-ghost"]));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Stopped);
        roster.Players.Should().BeEmpty();
        roster.Count.Should().BeNull();
    }

    /// <summary>
    /// Stopped is decided before observability, so a game nobody can see into still gets a real answer
    /// when it is switched off.
    /// </summary>
    [Fact]
    public async Task AStoppedUnobservableServerIsStoppedNotUnknown()
    {
        Inventory("starbound");
        Running("starbound", false);
        Presence(("starbound", "none", []));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Stopped);
    }

    /// <summary>
    /// A run state that could not be read is not a stopped server. It falls through to whatever
    /// presence can say, because "we could not ask the engine" is not "the answer is off".
    /// </summary>
    [Fact]
    public async Task AnUnreadableRunStateDoesNotBecomeStopped()
    {
        Inventory("minecraft");
        RunStateUnreadable("minecraft");
        Presence(("minecraft", "log", ["alice"]));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Known);
        roster.Count.Should().Be(1);
    }

    // ── detection spellings ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// RCON-polled presence is presence. This is the case that used to read as unknowable, and the
    /// reason detection comes from the supervisor rather than from the instance's regex fields.
    /// </summary>
    [Fact]
    public async Task RconPolledPresenceIsMeasured()
    {
        Inventory("projectzomboid");
        Running("projectzomboid", true);
        Presence(("projectzomboid", "rcon", ["Walterus"]));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Known);
        roster.Count.Should().Be(1);
    }

    // ── what a player is called ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A session with no name and no id is counted and left unnamed. <b>The address is deliberately
    /// not a fallback label</b> — it identifies a connection rather than a person, and putting one in
    /// a chat message publishes a player's IP to the whole channel.
    /// </summary>
    [Fact]
    public async Task APlayerTheGameDidNotNameIsCountedButNotLabelled()
    {
        Inventory("romestead");
        Running("romestead", true);
        _watchdog.GetPlayerPresenceAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, WatchdogInstancePresence>
            {
                ["romestead"] = new()
                {
                    Detection = "log",
                    Players = [new WatchdogPlayer { Addr = "203.0.113.7:51820" }],
                },
            } as IReadOnlyDictionary<string, WatchdogInstancePresence>);

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Count.Should().Be(1);
        roster.Players.Single().Label.Should().BeNull();
    }

    [Fact]
    public async Task AnIdStandsInWhenTheGameGivesNoName()
    {
        Inventory("romestead");
        Running("romestead", true);
        _watchdog.GetPlayerPresenceAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, WatchdogInstancePresence>
            {
                ["romestead"] = new()
                {
                    Detection = "log",
                    Players = [new WatchdogPlayer { Id = "76561198000000000" }],
                },
            } as IReadOnlyDictionary<string, WatchdogInstancePresence>);

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Players.Single().Label.Should().Be("76561198000000000");
    }

    // ── run state, carried ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The run state this join already had to read is handed back with the answer, so a caller that
    /// wants both — the live status board wants exactly both — does not ask the engine a second time
    /// and cannot get a second answer about the same moment.
    /// </summary>
    [Fact]
    public async Task TheRunStateBehindTheAnswerIsCarriedWithIt()
    {
        Inventory("minecraft", "terraria");
        Running("minecraft", true);
        Running("terraria", false);
        Presence(("minecraft", "log", ["alice"]), ("terraria", "log", []));

        IReadOnlyList<ServerRoster> rosters = await Roster().GetAllAsync();

        rosters.Single(r => r.Server == "minecraft").Running.Should().BeTrue();
        rosters.Single(r => r.Server == "terraria").Running.Should().BeFalse();
    }

    [Fact]
    public async Task AnUnreadRunStateIsCarriedAsUnknownRatherThanAsStopped()
    {
        Inventory("minecraft");
        RunStateUnreadable("minecraft");
        Presence(("minecraft", "log", ["alice"]));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Running.Should().BeNull();
        roster.Knowledge.Should().Be(RosterKnowledge.Known);
    }

    /// <summary>
    /// Every state carries it, not just the measured one — a caller reading run state must not have to
    /// know which roster answers happen to include it.
    /// </summary>
    [Fact]
    public async Task ARunningServerNobodyCanSeeIntoStillReportsThatItIsRunning()
    {
        Inventory("starbound");
        Running("starbound", true);
        Presence(("starbound", "none", []));

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.NotObservable);
        roster.Running.Should().BeTrue();
        roster.Count.Should().BeNull();
    }

    [Fact]
    public async Task ARunningServerReportsThatWithTheSupervisorDown()
    {
        Inventory("minecraft");
        Running("minecraft", true);
        SupervisorDown();

        ServerRoster roster = (await Roster().GetAllAsync()).Single();

        roster.Knowledge.Should().Be(RosterKnowledge.Unavailable);
        roster.Running.Should().BeTrue();
    }

    // ── lookup ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AServerThisHostDoesNotHaveIsNull()
    {
        _cache.GetInstanceAsync("nope", Arg.Any<CancellationToken>()).Returns((Instance?)null);

        (await Roster().GetAsync("nope")).Should().BeNull();
    }
}
