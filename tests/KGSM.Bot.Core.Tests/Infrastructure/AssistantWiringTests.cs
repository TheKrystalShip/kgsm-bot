using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// The assistant client resolves from the container the bot actually builds, on a host that has an
/// assistant and on one that does not.
/// </summary>
/// <remarks>
/// A half-wired graph is the failure this catches: the client is what the @-mention surface asks
/// for, and a registration that resolves only when configured would leave a host without an
/// assistant unable to construct its message handler at all — a startup crash, not a quiet surface.
/// </remarks>
public class AssistantWiringTests
{
    private static IServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["KGSM:Path"] = "/usr/local/bin/kgsm" };
        foreach (var (key, value) in settings)
            values[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureServices(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void WithAnAssistantConfigured_TheClientResolvesArmed()
    {
        using var provider = (ServiceProvider)Build(
            ("Assistant:BaseUrl", "http://127.0.0.1:5180"),
            ("Assistant:RelaySecret", "s3cret"));

        provider.GetRequiredService<IAssistantTurnClient>().IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void WithNoAssistantOnThisHost_TheClientStillResolves_AndReportsItselfOff()
    {
        using var provider = (ServiceProvider)Build();

        provider.GetRequiredService<IAssistantTurnClient>().IsConfigured.Should().BeFalse();
    }
}
