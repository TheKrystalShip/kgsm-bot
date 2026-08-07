using KGSM.Bot.Infrastructure;
using KGSM.Bot.Application;

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
        // The Control Panel lists this bot's commands from a file the deploy ships, and that file is
        // written by the build running the binary it just produced. Reflection over this assembly and
        // nothing else: no host, no configuration, no Discord connection, no side effect but the file.
        if (args is ["--emit-commands", string manifestPath])
        {
            Commands.CommandManifest.WriteTo(manifestPath);
            return;
        }

        // Create and configure the host
        using var host = CreateHostBuilder(args).Build();

        // Start the host
        await host.RunAsync();
    }

    /// <summary>The file declaring the bot's whole configurable surface, shipped beside the binary.</summary>
    private const string SettingsFile = "kgsm-bot.settings.json";

    /// <summary>
    /// Creates and configures the host builder
    /// </summary>
    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                // Resolved against the binary's own directory, not the process working directory:
                // under systemd those are not the same place, and a relative path would make the
                // bot start with none of its configuration rather than fail.
                config.AddJsonFile(Path.Combine(AppContext.BaseDirectory, SettingsFile),
                    optional: false,
                    reloadOnChange: true);
                config.AddJsonFile(
                    Path.Combine(AppContext.BaseDirectory,
                        $"kgsm-bot.settings.{context.HostingEnvironment.EnvironmentName}.json"),
                    optional: true,
                    reloadOnChange: true);

                // Last of the two, so the env file and the unit still override the file above:
                // a source added later wins, and CreateDefaultBuilder already added this one.
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

                // Listens for @-mentions and puts them to the assistant leaf. The client itself is
                // registered with the infrastructure, beside the rest of this host's outward wiring.
                services.AddSingleton<MessageHandler>();

                // Register hosted service
                services.AddHostedService<BotService>();
            });
}
