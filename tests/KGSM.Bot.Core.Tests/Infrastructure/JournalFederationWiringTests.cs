using FluentAssertions;

using KGSM.Bot.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Services;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// The bot reads every producer's journal, not the engine's alone — asserted on the container the
/// bot actually builds.
/// </summary>
/// <remarks>
/// <para>
/// <b>This guards a failure with no symptom.</b> Half of what this bot announces is not the engine's
/// to say: the supervisor owns crashes, give-ups and player presence. Registering the federated pair
/// before <c>AddKgsmServices</c> rather than after is a one-line mistake that throws nothing, logs
/// nothing and builds cleanly — the single-journal registration simply wins, and Discord goes quiet
/// about incidents while continuing to announce installs and backups perfectly. From inside a
/// channel that is indistinguishable from a host where nothing has gone wrong.
/// </para>
/// <para>
/// Both halves are asserted because both moved and both matter: the tail is what announces, and
/// <c>/history</c> reads back over the same record. A history that could not show the crash somebody
/// was just told about is the more confusing of the two failures.
/// </para>
/// <para>
/// Resolving is the assertion, not construction — a federated source composes one reader per journal
/// it finds on disk, and finding none but the engine's still yields the federated type. Nothing here
/// starts a read loop, so no journal is opened.
/// </para>
/// </remarks>
public class JournalFederationWiringTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM:Path"] = "/usr/local/bin/kgsm",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureServices(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void TheLiveTail_ReadsEveryProducersJournal()
    {
        using var provider = Build();

        provider.GetRequiredService<IEventSource>().Should().BeOfType<FederatedEventSource>(
            "crashes, give-ups and player presence are the supervisor's events, and a source reading "
            + "only the engine's journal announces none of them while looking perfectly healthy");
    }

    [Fact]
    public void ReadingHistoryBack_CoversEveryProducersJournal()
    {
        using var provider = Build();

        provider.GetRequiredService<IEventJournalHistory>()
            .Should().BeOfType<FederatedEventJournalHistory>(
                "/history answers out of the same record the announcements tail, and must not be "
                + "able to show less of an incident than the channel already reported");
    }
}
