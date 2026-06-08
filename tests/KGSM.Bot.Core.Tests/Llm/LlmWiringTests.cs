using FluentAssertions;

using KGSM.Bot.Discord.Llm;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;

using Xunit;

namespace KGSM.Bot.Tests.Llm;

/// <summary>
/// Verifies the LLM dependency-injection seam: the library's
/// <c>AddLocalLlm</c> plus the bot's <see cref="ServerAssistant"/> compose into a
/// resolvable graph. Unit tests elsewhere mock the agent, so they bypass the real
/// container; this exercises it. The kgsm-specific dependencies
/// (<see cref="IToolDispatcher"/>, <see cref="ISystemPromptBuilder"/>) are stubbed
/// so this isolates the new wiring rather than the full kgsm stack.
/// </summary>
public class LlmWiringTests
{
    [Fact]
    public void AddLocalLlm_PlusServerAssistant_ResolvesTheAgentGraph()
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
        services.AddLocalLlm(configuration);

        // Stub the kgsm-specific collaborators so only the LLM seam is under test.
        services.AddSingleton(Substitute.For<IToolDispatcher>());
        services.AddSingleton(Substitute.For<ISystemPromptBuilder>());
        services.AddSingleton<IConfirmationContext, ConfirmationContext>();
        services.AddSingleton<IServerAssistant, ServerAssistant>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        // The whole chain MessageHandler depends on must resolve from the container.
        provider.GetRequiredService<IServerAssistant>().Should().NotBeNull();
        provider.GetRequiredService<ILlmAgent>().Should().NotBeNull();
        provider.GetRequiredService<ILlmClient>().Should().NotBeNull();
        provider.GetRequiredService<IConversationStore>().Should().NotBeNull();
    }
}
