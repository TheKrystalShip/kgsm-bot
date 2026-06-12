using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;
using Internal = KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord;
using KGSM.Bot.Infrastructure.KGSM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Discord;
using Discord.WebSocket;
using Discord.Interactions;

using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.Llm.Extensions;

namespace KGSM.Bot.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure services
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register configuration
        services.Configure<DiscordOptions>(
            configuration.GetSection(DiscordOptions.Section));

        services.Configure<Internal.KgsmOptions>(
            configuration.GetSection(Internal.KgsmOptions.Section));

        services.Configure<KgsmCacheOptions>(
            configuration.GetSection(KgsmCacheOptions.Section));

        // Register Discord services
        services.AddDiscordServices();

        // Register the reusable local-LLM stack (Ollama client, conversation store,
        // agent loop). Binds the "Ollama", "Conversation", and "LlmAgent" config
        // sections. The kgsm-specific IToolDispatcher is registered in Program.
        services.AddLocalLlm(configuration);

        // Register KGSM services
        services.AddKgsmServices(configuration);

        return services;
    }

    private static IServiceCollection AddDiscordServices(this IServiceCollection services)
    {
        // Discord.Net services
        services.AddSingleton<DiscordSocketConfig>(sp => new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info
        });

        services.AddSingleton<DiscordSocketClient>();

        services.AddSingleton<InteractionServiceConfig>(sp => new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async,
            LogLevel = LogSeverity.Debug
        });

        services.AddSingleton<InteractionService>(sp =>
            new InteractionService(
                sp.GetRequiredService<DiscordSocketClient>(),
                sp.GetRequiredService<InteractionServiceConfig>()));

        // Application service implementations
        services.AddSingleton<IDiscordChannelRegistry, DiscordChannelRegistry>();
        services.AddSingleton<IDiscordNotificationService, DiscordNotificationService>();

        return services;
    }

    private static IServiceCollection AddKgsmServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Get KGSM configuration
        var kgsmOptions = configuration
            .GetSection(Internal.KgsmOptions.Section)
            .Get<Internal.KgsmOptions>() ?? throw new InvalidOperationException("KGSM configuration is missing or invalid");

        // Add KGSM-Lib services
        services.AddKgsmServices(kgsmOptions.Path, kgsmOptions.SocketPath);

        // Typed client for the kgsm-watchdog control socket (read-only supervision
        // surface). Registration always succeeds — the socket path defaults to the
        // daemon's own default — and an absent/unreachable daemon is handled at call
        // time, so this is safe even on hosts where the watchdog isn't deployed.
        services.AddKgsmWatchdogClient(kgsmOptions.WatchdogSocketPath);

        // Application service implementations
        services.AddSingleton<IServerEventHandler, KgsmServerEventHandler>();
        services.AddSingleton<IBlueprintService, KgsmBlueprintService>();
        services.AddSingleton<IServerInstanceService, KgsmServerInstanceService>();
        services.AddSingleton<IWatchdogService, WatchdogService>();

        // Cached inventory (avoids spawning kgsm per message)
        services.AddSingleton<IKgsmStateCache, KgsmStateCache>();

        return services;
    }
}
