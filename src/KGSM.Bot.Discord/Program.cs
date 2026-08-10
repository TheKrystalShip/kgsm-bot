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

        // Moving a host that was wired to one Discord server into the guild store: it reads the old
        // keys, prints every row it would write, and touches nothing without --apply. A one-off, so it
        // runs here rather than behind a host it does not need.
        if (args is [_, ..] && args[0] == "--adopt-guild-config")
        {
            Environment.ExitCode = GuildConfigAdoption.Run(
                settingsPath: ValueAfter(args, "--from") ?? Path.Combine(AppContext.BaseDirectory, SettingsFile),
                announceChannelOverride: ulong.TryParse(ValueAfter(args, "--announce-channel"), out ulong c) ? c : 0,
                apply: args.Contains("--apply"));
            return;
        }

        // Create and configure the host
        using var host = CreateHostBuilder(args).Build();

        // Start the host
        await host.RunAsync();
    }

    /// <summary>The file declaring the bot's whole configurable surface, shipped beside the binary.</summary>
    private const string SettingsFile = "kgsm-bot.settings.json";

    /// <summary>The value of a <c>--flag value</c> pair, or null when the flag is absent.</summary>
    private static string? ValueAfter(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

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

                // Publishes the bot's status on a unix socket for the Control Panel to read. Runs
                // beside the bot rather than inside it: it must be able to report a gateway that never
                // connected, which a service hanging off the client's Ready event could not.
                services.AddHostedService<StatusSocketServer>();
            });
}
