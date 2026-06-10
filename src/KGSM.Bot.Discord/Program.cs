using KGSM.Bot.Infrastructure;
using KGSM.Bot.Application;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Discord;

/// <summary>
/// Main program
/// </summary>
public class Program
{
    /// <summary>
    /// Application entry point
    /// </summary>
    public static async Task Main(string[] args)
    {
        // Create and configure the host
        using var host = CreateHostBuilder(args).Build();

        // Start the host
        await host.RunAsync();
    }

    /// <summary>
    /// Creates and configures the host builder
    /// </summary>
    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
                    optional: true,
                    reloadOnChange: true);
                config.AddEnvironmentVariables();

                if (args != null)
                {
                    config.AddCommandLine(args);
                }
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                logging.AddConsole();
                logging.AddDebug();
            })
            .ConfigureServices((context, services) =>
            {
                // Register application services
                services.AddApplicationServices();

                // Register infrastructure services
                services.AddInfrastructureServices(context.Configuration);

                // Register the interaction handler
                services.AddSingleton<InteractionHandler>();

                // The reusable agent loop, Ollama client, and conversation store are
                // registered by AddLocalLlm (inside AddInfrastructureServices). Here we
                // add the extracted kgsm assistant (prompt builder, tool dispatcher,
                // policy) and the bot's adapters that satisfy its IServerOperations /
                // IServerInventory ports over the existing MediatR + state cache.
                services.AddKgsmAssistant();
                services.AddSingleton<IServerOperations, Llm.MediatorServerOperations>();
                services.AddSingleton<IServerInventory, Llm.StateCacheInventory>();

                // Register the message handler (LLM bridge)
                services.AddSingleton<MessageHandler>();

                // Register hosted service
                services.AddHostedService<BotService>();
            });
}
