using System.Net.Http;

using FluentAssertions;

using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// Covers how <see cref="WatchdogService"/> maps the typed client's three transport
/// outcomes onto the bot's result model: a tracked instance and an untracked-but-
/// reachable daemon are both successes (Supervised / NotSupervised), while an
/// unreachable daemon is the one genuine failure (so the bot never reports "not
/// supervised" when it actually couldn't reach the daemon to find out).
/// </summary>
public class WatchdogServiceTests
{
    private readonly IWatchdogClient _client = Substitute.For<IWatchdogClient>();

    private WatchdogService Create() => new(_client, NullLogger<WatchdogService>.Instance);

    [Fact]
    public async Task GetStatusAsync_WhenDaemonTracksInstance_ReturnsSupervisedState()
    {
        var state = new WatchdogInstanceState
        {
            Name = "7dtd",
            Desired = "running",
            Populated = true,
            Phase = "running",
            Restarts = 0,
            Pid = 4242,
        };
        _client.GetStatusAsync("7dtd").Returns(state);

        var result = await Create().GetStatusAsync("7dtd");

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsSupervised.Should().BeTrue();
        result.Value.State.Should().BeSameAs(state);
    }

    [Fact]
    public async Task GetStatusAsync_WhenDaemonDoesNotTrackInstance_ReturnsNotSupervised()
    {
        // null == HTTP 404 from the daemon: up, but not tracking this instance.
        _client.GetStatusAsync("orphan").Returns((WatchdogInstanceState?)null);

        var result = await Create().GetStatusAsync("orphan");

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsSupervised.Should().BeFalse();
        result.Value.State.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_WhenDaemonUnreachable_ReturnsFailure()
    {
        _client.GetStatusAsync("7dtd").ThrowsAsync(new HttpRequestException("connection refused"));

        var result = await Create().GetStatusAsync("7dtd");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not reachable");
    }
}
