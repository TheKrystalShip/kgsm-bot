using FluentAssertions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Tests.Application;

/// <summary>
/// Guards the health snapshot path: that ServerService.GetHealthSnapshotAsync
/// correctly fetches the runtime status + host disk and maps them into the
/// neutral InstanceHealthSnapshot.
/// </summary>
public class HealthSnapshotTests
{
    [Fact]
    public async Task GetHealthSnapshotAsync_ReturnsSnapshot()
    {
        var serverInstanceService = Substitute.For<IServerInstanceService>();
        serverInstanceService.GetRuntimeStatusAsync("minecraft").Returns(Task.FromResult(Result.Success(
            new InstanceRuntimeStatus
            {
                InstanceName = "minecraft",
                Status = true,
                Version = new VersionInfo { Current = "1.0.0", Checked = false, UpdatesAvailable = null },
                RecentLogs = "",
            })));
        serverInstanceService.GetSystemInfoAsync().Returns(Task.FromResult(Result.Failure<SystemInfo>("n/a")));

        var stateCache = Substitute.For<IKgsmStateCache>();
        var watchdogService = Substitute.For<IWatchdogService>();

        var services = new ServiceCollection();
        services.AddSingleton(serverInstanceService);
        services.AddSingleton(stateCache);
        services.AddSingleton(watchdogService);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IServerService, ServerService>();

        using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<IServerService>();

        var result = await server.GetHealthSnapshotAsync("minecraft");

        result.IsSuccess.Should().BeTrue(
            "ServerService must correctly fetch and map the health snapshot");
        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Running.Should().BeTrue();
    }
}
