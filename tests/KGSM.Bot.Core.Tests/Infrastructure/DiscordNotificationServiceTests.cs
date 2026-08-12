using Discord.WebSocket;

using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// Where an announcement goes, and what the bot claims about having sent it.
/// </summary>
/// <remarks>
/// The Discord client here is a real, unconnected <see cref="DiscordSocketClient"/>, so it resolves
/// no channel — which is exactly the state these tests are about. Whether the message left is not
/// the interesting part; whether the bot <i>says</i> it did is, because a bare success covering a
/// guild that silently received nothing is a fabricated status.
/// </remarks>
public sealed class DiscordNotificationServiceTests : IDisposable
{
    private readonly DiscordSocketClient _client = new();
    private readonly IGuildStore _guilds = Substitute.For<IGuildStore>();
    private readonly IIncidentTriage _triage = Substitute.For<IIncidentTriage>();
    private readonly DiscordOptions _options = new();

    /// <summary>
    /// The real queue rather than a substitute. Nothing here reaches it — every send is refused at
    /// channel resolution first — but a stand-in returning a null task would turn a test that did
    /// reach it into a null reference instead of the answer it was asking for.
    /// </summary>
    private readonly DiscordSendQueue _queue;

    public DiscordNotificationServiceTests()
    {
        _queue = new DiscordSendQueue(Options.Create(_options), NullLogger<DiscordSendQueue>.Instance);

        // A guild that has set no filter follows everything, which is what the real store answers for
        // a guild with no rows and what every test here but the filtering ones assumes. A substitute
        // defaults a bool to false, and false here is a bot that announces nowhere.
        _guilds.Follows(Arg.Any<ulong>(), Arg.Any<string>()).Returns(true);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _client.Dispose();
    }

    private DiscordNotificationService Service() => new(
        _client, _guilds, _queue, _triage, Options.Create(_options),
        NullLogger<DiscordNotificationService>.Instance);

    private static GuildTopology Guild(ulong id, ulong announce, ulong? board = null) =>
        new(id, announce, board, "heisen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ServerAnnouncement Started(string instance = "factorio") =>
        new(AnnouncementKind.Started, instance, null, null);

    /// <summary>
    /// A bot invited to guilds and set up in none of them is deliberately silent, and that is a
    /// success — nothing was asked of it and nothing was missed.
    /// </summary>
    [Fact]
    public async Task NoGuildConfiguredAnnouncesNowhereAndSaysSo()
    {
        _guilds.Configured().Returns([]);

        Result result = await Service().AnnounceAsync(Started());

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// The switch is checked before anything else: a kind the operator turned off costs no store read
    /// and no send.
    /// </summary>
    [Fact]
    public async Task ASwitchedOffKindNeverReachesTheStore()
    {
        _options.Announce.PlayerJoined = false;

        Result result = await Service()
            .AnnounceAsync(new ServerAnnouncement(AnnouncementKind.PlayerJoined, "factorio", null, null));

        result.IsSuccess.Should().BeTrue();
        _guilds.DidNotReceive().Configured();
    }

    /// <summary>
    /// Every configured guild is tried, and a guild that could not be reached is counted against the
    /// ones that were. Reporting success here would be the difference between "nobody heard" and
    /// "everybody heard", stated as the same thing.
    /// </summary>
    [Fact]
    public async Task AGuildThatCannotBeReachedIsCountedNotSwallowed()
    {
        _guilds.Configured().Returns([Guild(1, 10), Guild(2, 20)]);

        Result result = await Service().AnnounceAsync(Started());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("0 of 2");
        // Both were attempted — one unreachable guild must not stop the next.
        result.Error.Should().Contain("1:").And.Contain("2:");
    }

    /// <summary>
    /// A server's own channel is a preference, and a guild's announcement channel is the requirement.
    /// A binding left pointing at a deleted channel must not be the end of the attempt — that loses
    /// every announcement about that server, silently, for as long as nobody notices.
    /// </summary>
    /// <remarks>
    /// Nothing is connected here, so both lookups miss and the announcement fails either way. What is
    /// under test is that the failure is about <i>both</i> channels rather than only the bound one,
    /// which is the difference between falling back and giving up.
    /// </remarks>
    [Fact]
    public async Task ABoundChannelThatCannotBeSeenFallsBackToTheAnnouncementChannel()
    {
        _guilds.Configured().Returns([Guild(1, 10, board: 99)]);
        _guilds.ChannelFor(1, "factorio").Returns(111ul);

        Result result = await Service().AnnounceAsync(Started());

        result.Error.Should().Contain("announcement channel");
    }

    /// <summary>
    /// A server reports in its own channel where the guild runs a board and has bound one, and in the
    /// guild's announcement channel everywhere else — including in a guild that runs a board but only
    /// started doing so after that server was installed.
    /// </summary>
    [Fact]
    public async Task TheChannelIsTheServersOwnWhereItHasOneAndTheGuildsOtherwise()
    {
        _guilds.Configured().Returns([Guild(1, 10, board: 99), Guild(2, 20)]);
        _guilds.ChannelFor(1, "factorio").Returns(111ul);
        _guilds.ChannelFor(2, "factorio").Returns((ulong?)null);

        await Service().AnnounceAsync(Started());

        // The resolution is what is under test; the send fails for both because nothing is connected.
        _guilds.Received(1).ChannelFor(1, "factorio");
        _guilds.Received(1).ChannelFor(2, "factorio");
    }

    // ── the per-guild server filter ───────────────────────────────────────────────────────────

    /// <summary>
    /// A guild that does not follow this server is not reached for it at all — not resolved, not sent
    /// to, and not counted.
    /// </summary>
    [Fact]
    public async Task AGuildThatDoesNotFollowTheServerHearsNothingAboutIt()
    {
        _guilds.Configured().Returns([Guild(1, 10), Guild(2, 20)]);
        _guilds.Follows(2, "factorio").Returns(false);

        Result result = await Service().AnnounceAsync(Started());

        _guilds.Received(1).ChannelFor(1, "factorio");
        _guilds.DidNotReceive().ChannelFor(2, "factorio");

        // Counted against the guilds that follow it, not against every guild configured.
        result.Error.Should().Contain("0 of 1");
    }

    /// <summary>
    /// <b>A filter working is not a delivery failing.</b> Folding the two together would report every
    /// announcement as a partial failure for the whole life of a host where one guild follows one
    /// game, and an operator reading that log would go looking for a fault that is not there.
    /// </summary>
    [Fact]
    public async Task NoGuildFollowingTheServerIsASuccess()
    {
        _guilds.Configured().Returns([Guild(1, 10), Guild(2, 20)]);
        _guilds.Follows(Arg.Any<ulong>(), "factorio").Returns(false);

        Result result = await Service().AnnounceAsync(Started());

        result.IsSuccess.Should().BeTrue();
        _guilds.DidNotReceive().ChannelFor(Arg.Any<ulong>(), Arg.Any<string>());
    }

    /// <summary>
    /// The filter is per server, not per guild: the same guild that hears nothing about one game still
    /// hears about the ones it follows.
    /// </summary>
    [Fact]
    public async Task TheFilterIsAboutTheServerTheAnnouncementIsFor()
    {
        _guilds.Configured().Returns([Guild(1, 10)]);
        _guilds.Follows(1, "factorio").Returns(false);
        _guilds.Follows(1, "terraria").Returns(true);

        await Service().AnnounceAsync(Started("factorio"));
        await Service().AnnounceAsync(Started("terraria"));

        _guilds.DidNotReceive().ChannelFor(1, "factorio");
        _guilds.Received(1).ChannelFor(1, "terraria");
    }
}
