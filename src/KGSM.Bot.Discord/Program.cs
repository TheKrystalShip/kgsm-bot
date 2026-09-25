using KGSM.Bot.Core.Interfaces;

using TheKrystalShip.Discord.Voice;
using KGSM.Bot.Infrastructure;
using KGSM.Bot.Application;

using KGSM.Bot.Infrastructure.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.ComponentSurface;
using TheKrystalShip.KGSM.ComponentSurface.Http;
using TheKrystalShip.KGSM.Lifecycle;

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

        // The last thing this bot says. A consumer reading it knows the surface went quiet because
        // somebody stopped it, rather than because it is in one of the several states where it is
        // running and unable to post anything.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            host.Services.GetRequiredService<LeafLifecycle>().MarkStopping(LeafStopReason.Signal));

        // A socket only exists once Kestrel is listening, so the mode is set here rather than at bind
        // time — which would be an ENOENT on a file that is not there yet.
        KgsmOptions kgsm = host.Services.GetRequiredService<IOptions<KgsmOptions>>().Value;
        ILogger<Program> log = host.Services.GetRequiredService<ILogger<Program>>();
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
        {
            foreach (string socket in (string[])[kgsm.StatusSocketPath, kgsm.SurfaceSocketPath])
            {
                try
                {
                    if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(socket) && File.Exists(socket))
                        File.SetUnixFileMode(socket, SocketMode);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "could not set mode on {Socket}", socket);
                }
            }
        });

        // Start the host
        await host.RunAsync();
    }

    /// <summary>The file declaring the bot's whole configurable surface, shipped beside the binary.</summary>
    private const string SettingsFile = "kgsm-bot.settings.json";

    /// <summary>This component's id — what names its descriptor, its runtime directory and its unit.</summary>
    private const string ComponentId = "bot";

    /// <summary>
    /// Permission bits every socket this bot binds is given: readable and writable by the owner and by
    /// anything in its group, and by nothing else on the host. The node's API runs in that group.
    /// </summary>
    private const UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// The KGSM section as it stands once every configuration source has had its say, read where a
    /// bound <c>IOptions</c> is not available yet.
    /// </summary>
    private static KgsmOptions Bound(IConfiguration configuration) =>
        configuration.GetSection(KgsmOptions.Section).Get<KgsmOptions>() ?? new KgsmOptions();

    /// <summary>
    /// One unix socket as a listening address, with its directory made and any stale file cleared —
    /// or blank when there is nothing to serve there or it could not be prepared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An address rather than a <c>Listen</c> call.</b> Kestrel ignores the configured addresses
    /// entirely once anything has been bound through <c>KestrelServerOptions.Listen*</c>, so binding
    /// these sockets that way would silently unbind the member wire — the bot would run, answer about
    /// itself, and never again hear that somebody was demoted.
    /// </para>
    /// <para>
    /// A blank path is how a host says it wants that socket not served at all. A socket file left
    /// behind by a killed process would otherwise make the bind fail, and the bot's job is Discord:
    /// failing to publish a status must never stop it doing that.
    /// </para>
    /// </remarks>
    private static string UnixAddress(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(path))
                File.Delete(path);

            return "http://unix:" + path;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"<4>could not prepare {path}: {ex.Message}");
            return "";
        }
    }

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

                // Everything this bot listens on, joined into the one key the listener reads its
                // addresses from: the member wire under Cluster:Urls with every other fact about its
                // membership, and two unix sockets of its own. Joined here, once, so no two of them can
                // disagree — and added last, after the file and the environment, so it is the resolved
                // values that are joined rather than defaults.
                IConfigurationRoot resolved = config.Build();
                KgsmOptions kgsm = Bound(resolved);

                string clusterUrls =
                    resolved[$"{BotClusterOptions.Section}:{nameof(BotClusterOptions.Urls)}"]
                        is { Length: > 0 } urls ? urls : new BotClusterOptions().Urls;

                string[] listening =
                [
                    clusterUrls,
                    UnixAddress(kgsm.StatusSocketPath),
                    UnixAddress(kgsm.SurfaceSocketPath),
                ];

                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [WebHostDefaults.ServerUrlsKey] =
                        string.Join(';', listening.Where(a => a.Length > 0)),
                });
            })
            // What each of those three listeners serves.
            //
            // The member wire carries the cluster's inbox: a member of a cluster is pushed to rather
            // than polling — the auth anchor fans an account change out to every member's inbox, and a
            // member with nowhere to be reached would hold whatever it copied when it joined and never
            // hear that somebody was demoted.
            //
            // The other two are this bot's own: what it answers about the gateway, and what it answers
            // about ITSELF. A component owns its configuration, its unit and its journal wherever it
            // runs and only the transport differs; this is a leaf, so the node's API relays over the
            // surface socket rather than reading the descriptor for it, and finds that socket from this
            // component's id alone.
            .ConfigureWebHostDefaults(web => web
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapClusterEndpoints();

                        // Reachable over this bot's own sockets and nowhere else. The member wire is a
                        // network address whose callers are other members; these two answer about this
                        // host, and the socket's filesystem permissions are their whole boundary — so
                        // arriving anywhere but on a unix socket is not a route at all.
                        endpoints.MapGet("/status", (BotStatusReporter reporter) =>
                            Results.Json(reporter.Snapshot(), BotStatusJsonContext.Default.BotStatus))
                            .AddEndpointFilter<OwnSocketOnly>();

                        endpoints.MapGroup("/component")
                            .AddEndpointFilter<OwnSocketOnly>()
                            .MapComponentSurface();
                    });
                }))
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

                // What a spoken request turns into. Registered here rather than with the rest of the
                // voice wiring because answering one is a Discord concern: it posts into the voice
                // channel's chat and offers the same confirmation buttons the @-mention surface does.
                services.AddSingleton<IVoiceCommandHandler, Voice.AssistantVoiceCommandHandler>();


                // Register hosted service
                services.AddHostedService<BotService>();

                // What this bot says about itself when the Control Panel asks. Built beside the bot
                // rather than inside it: it must be able to report a gateway that never connected,
                // which a service hanging off the client's Ready event could not.
                services.AddSingleton<BotStatusReporter>();

                // The descriptor this build generated, the host's deploy floors beneath it, the
                // overrides in force, this unit's journal, and the bounce that makes a change take
                // effect. All of it is the shared component library, which is also what the generator
                // that writes the descriptor lives beside.
                KgsmOptions kgsm = Bound(context.Configuration);
                services.AddComponentSurface(new ComponentSurfaceOptions(
                    ComponentSurfacePaths.Descriptor(ComponentId),
                    kgsm.ConfigOverridePath,
                    ComponentSurfacePaths.Commands(ComponentId)));

                // The same facts, reported rather than only served. Nothing polls the status socket
                // on a schedule, so a bot that went silent at three in the morning stayed silent
                // until somebody opened a panel. Beside the bot for the same reason the socket is:
                // it must be able to report a gateway that never connected.
                services.AddHostedService<BotLifecycleReporter>();
            });
}
