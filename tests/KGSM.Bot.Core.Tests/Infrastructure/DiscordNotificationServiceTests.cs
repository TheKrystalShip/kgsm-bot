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
    private readonly DiscordOptions _options = new();

    /// <summary>
    /// The real queue rather than a substitute. Nothing here reaches it — every send is refused at
    /// channel resolution first — but a stand-in returning a null task would turn a test that did
    /// reach it into a null reference instead of the answer it was asking for.
    /// </summary>
    private readonly DiscordSendQueue _queue;

    public DiscordNotificationServiceTests() =>
        _queue = new DiscordSendQueue(Options.Create(_options), NullLogger<DiscordSendQueue>.Instance);

    public void Dispose()
    {
        _queue.Dispose();
        _client.Dispose();
    }

    private DiscordNotificationService Service() => new(
        _client, _guilds, _queue, Options.Create(_options),
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
}
