using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Configuration;
using Internal = KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord;
using KGSM.Bot.Infrastructure.KGSM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Discord;
using Discord.WebSocket;
using Discord.Interactions;

using TheKrystalShip.KGSM.Extensions;

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

        services.Configure<AssistantOptions>(
            configuration.GetSection(AssistantOptions.Section));

        // The line to the assistant leaf. Registered whether or not this host has one: the client
        // reports itself unconfigured and the conversational surface stays off, which is what
        // "a leaf runs standalone" means for an optional sibling.
        services.AddSingleton<IAssistantTurnClient, Assistant.AssistantTurnClient>();

        // This host's KGSM accounts — the one answer to who may act, shared with the Control Panel
        // and the assistant. A singleton because it holds the open store; opening it is what can
        // fail, and it fails into an unavailable directory rather than out of the constructor.
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddSingleton<IKgsmAccounts, KgsmAccounts>();

        // Register Discord services
        services.AddDiscordServices();

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

        // Add KGSM-Lib services. Engine events come from the journal — a file every consumer
        // reads concurrently, with no socket to bind and nothing to reserve. Tail, with no
        // cursor: this surface ANNOUNCES, and an announcement is only meaningful while it is
        // current (see KgsmOptions.JournalDir).
        services.AddKgsmServices(new TheKrystalShip.KGSM.Core.Models.KgsmOptions
        {
            KgsmPath = kgsmOptions.Path,
            EventJournalDirectory = kgsmOptions.JournalDir,
            EventStartPosition = TheKrystalShip.KGSM.Core.Models.EventStartPosition.Tail
        });

        // Typed client for the kgsm-watchdog control socket (read-only supervision
        // surface). Registration always succeeds — the socket path defaults to the
        // daemon's own default — and an absent/unreachable daemon is handled at call
        // time, so this is safe even on hosts where the watchdog isn't deployed.
        services.AddKgsmWatchdogClient(kgsmOptions.WatchdogSocketPath);

        // Ambient provenance (who/through-what) for the current action — set at an entry point (a slash
        // command, the LLM message handler), read at the kgsm chokepoint so every mutation is attributable.
        // Singleton: the AsyncLocal inside isolates the value per request flow.
        services.AddSingleton<global::KGSM.Bot.Core.Common.IInvocationContext, global::KGSM.Bot.Core.Common.AsyncLocalInvocationContext>();

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
