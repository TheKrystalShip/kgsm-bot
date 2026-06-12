using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Extensions;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// Verifies the watchdog DI seam the bot adds in <c>AddKgsmServices</c>: kgsm-lib's
/// <c>AddKgsmWatchdogClient</c> plus the bot's <see cref="IWatchdogService"/> adapter
/// compose into a resolvable graph. Registration must succeed without a live daemon —
/// the socket is dialed per-request, not at construction — so this resolves the full
/// chain without one running.
/// </summary>
public class WatchdogWiringTests
{
    [Fact]
    public void AddKgsmWatchdogClient_PlusWatchdogService_ResolvesTheGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKgsmWatchdogClient("/run/kgsm-watchdog/control.sock");
        services.AddSingleton<IWatchdogService, WatchdogService>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<IWatchdogClient>().Should().NotBeNull();
        provider.GetRequiredService<IWatchdogService>().Should().NotBeNull();
    }
}
