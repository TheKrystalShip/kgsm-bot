using FluentAssertions;

using KGSM.Bot.Application.Queries;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using MediatR;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Tests.Application;

/// <summary>
/// Guards the one bot-path link that handler unit tests bypass: that
/// <c>GetHealthSnapshotQueryHandler</c> is actually discovered by the bot's MediatR
/// assembly-scan registration and dispatches end-to-end (MediatorServerOperations relies
/// on this). Without it, run_health_check would compile and unit-test green yet throw
/// "no handler" at runtime on Discord.
/// </summary>
public class HealthSnapshotMediatorWiringTests
{
    [Fact]
    public async Task GetHealthSnapshotQuery_IsDiscovered_AndDispatches()
    {
        var service = Substitute.For<IServerInstanceService>();
        service.GetRuntimeStatusAsync("minecraft").Returns(Task.FromResult(Result.Success(
            new InstanceRuntimeStatus
            {
                InstanceName = "minecraft",
                Status = true,
                Version = new VersionInfo { Current = "1.0.0", Checked = false, UpdatesAvailable = null },
                RecentLogs = "",
            })));
        service.GetSystemInfoAsync().Returns(Task.FromResult(Result.Failure<SystemInfo>("n/a")));

        var services = new ServiceCollection();
        // The exact registration the bot uses (KGSM.Bot.Application.DependencyInjection):
        // assembly-scan the Application assembly for handlers.
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(GetHealthSnapshotQuery).Assembly));
        services.AddSingleton(service);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        using var provider = services.BuildServiceProvider();

        var mediator = provider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new GetHealthSnapshotQuery("minecraft"));

        result.IsSuccess.Should().BeTrue(
            "the handler must be discovered by the bot's assembly-scan MediatR registration and run");
    }
}
