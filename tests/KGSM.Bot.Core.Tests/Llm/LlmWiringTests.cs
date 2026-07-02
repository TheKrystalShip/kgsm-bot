using FluentAssertions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Llm;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;

using Xunit;

namespace KGSM.Bot.Tests.Llm;

/// <summary>
/// Verifies the LLM dependency-injection seam end to end: the library's
/// <c>AddLocalLlm</c> + the assistant's <c>AddKgsmAssistant</c> + the bot's two
/// port adapters (<see cref="ServerOperations"/>, <see cref="StateCacheInventory"/>)
/// compose into a resolvable graph. The adapters' own dependencies (IServerService, the
/// state cache) are stubbed so this isolates the wiring rather than the full kgsm stack.
/// </summary>
public class LlmWiringTests
{
    [Fact]
    public void AddLocalLlm_PlusKgsmAssistant_ResolvesTheAgentGraph()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:Model"] = "test-model",
                ["Conversation:MaxMessages"] = "12",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLocalLlm(configuration);
        services.AddKgsmAssistant();

        // The bot's adapters that satisfy the assistant's host ports...
        services.AddSingleton<IServerOperations, ServerOperations>();
        services.AddSingleton<IServerInventory, StateCacheInventory>();

        // ...and stubs for what those adapters depend on, so only the wiring is under test.
        services.AddSingleton(Substitute.For<IServerService>());
        services.AddSingleton(Substitute.For<IKgsmStateCache>());

        using var provider = services.BuildServiceProvider(validateScopes: true);

        // The whole chain MessageHandler depends on must resolve from the container.
        provider.GetRequiredService<IServerAssistant>().Should().NotBeNull();
        provider.GetRequiredService<ILlmAgent>().Should().NotBeNull();
        provider.GetRequiredService<ILlmClient>().Should().NotBeNull();
        provider.GetRequiredService<IConversationStore>().Should().NotBeNull();
        provider.GetRequiredService<IToolDispatcher>().Should().NotBeNull();
        provider.GetRequiredService<IServerOperations>().Should().NotBeNull();
        provider.GetRequiredService<IServerInventory>().Should().NotBeNull();
    }
}
