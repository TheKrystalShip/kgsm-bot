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
/// The channel bindings, and the one operation that can destroy one.
/// </summary>
/// <remarks>
/// A binding ties a server to the channel holding its history, and it cannot be re-derived from
/// anywhere. Everything here is about the same rule: a binding is only ever dropped on an answer that
/// actually says the channel is gone, never on the absence of one.
/// </remarks>
public sealed class DiscordChannelRegistryTests : IDisposable
{
    private readonly DiscordSocketClient _client = new();
    private readonly IGuildStore _guilds = Substitute.For<IGuildStore>();
    private readonly DiscordOptions _options = new();
    private readonly DiscordSendQueue _queue;

    public DiscordChannelRegistryTests() =>
        _queue = new DiscordSendQueue(Options.Create(_options), NullLogger<DiscordSendQueue>.Instance);

    public void Dispose()
    {
        _queue.Dispose();
        _client.Dispose();
    }

    private DiscordChannelRegistry Registry() => new(
        _client, _guilds, _queue, Options.Create(_options),
        NullLogger<DiscordChannelRegistry>.Instance);

    private static GuildTopology Guild(ulong id, ulong announce, ulong? board = null) =>
        new(id, announce, board, "heisen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>
    /// <b>A guild the bot cannot see says nothing about its bindings.</b> Nothing is connected in this
    /// test, so every guild lookup misses — exactly the shape of a gateway outage — and reading that
    /// as "every channel in it is gone" would drop every binding the host has.
    /// </summary>
    [Fact]
    public async Task AGuildThatCannotBeSeenLosesNoBindings()
    {
        _guilds.Configured().Returns([Guild(1, 10, board: 99)]);
        _guilds.ChannelsIn(1).Returns([new GuildChannel(1, "factorio", 111, DateTimeOffset.UtcNow)]);

        Result result = await Registry().ReconcileBindingsAsync();

        result.IsSuccess.Should().BeTrue();
        _guilds.DidNotReceive().UnbindChannel(Arg.Any<ulong>(), Arg.Any<string>());
    }

    /// <summary>
    /// A host that has configured no guild has no bindings to reconcile, and asks Discord nothing.
    /// </summary>
    [Fact]
    public async Task NothingConfiguredIsNothingToDo()
    {
        _guilds.Configured().Returns([]);

        Result result = await Registry().ReconcileBindingsAsync();

        result.IsSuccess.Should().BeTrue();
        _guilds.DidNotReceive().ChannelsIn(Arg.Any<ulong>());
        _guilds.DidNotReceive().UnbindChannel(Arg.Any<ulong>(), Arg.Any<string>());
    }

    /// <summary>
    /// A guild that does not follow a server gets no channel for it — the loudest possible way to
    /// ignore the filter would be a channel in somebody's sidebar named after a game they said they
    /// did not want to hear about.
    /// </summary>
    [Fact]
    public async Task NoChannelIsMadeForAServerTheGuildDoesNotFollow()
    {
        _guilds.Configured().Returns([Guild(1, 10, board: 99)]);
        _guilds.Follows(1, "factorio").Returns(false);

        await Registry().AddOrUpdateChannelAsync("factorio");

        _guilds.DidNotReceive().ChannelFor(Arg.Any<ulong>(), Arg.Any<string>());
        _guilds.DidNotReceive().BindChannel(Arg.Any<ulong>(), Arg.Any<string>(), Arg.Any<ulong>());
    }
}
