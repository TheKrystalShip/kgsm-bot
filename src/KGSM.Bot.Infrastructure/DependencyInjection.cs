using TheKrystalShip.KGSM.Auth;
using KGSM.Bot.Core.Interfaces;
using TheKrystalShip.Discord.Voice;
using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Configuration;
using Internal = KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord;
using KGSM.Bot.Infrastructure.KGSM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Discord;
using Discord.WebSocket;
using Discord.Interactions;

using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Lifecycle;
using System.Reflection;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Dns.Member;

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
        // Membership. This bot asks the assistant on somebody's behalf, which is a member-to-member call:
        // it authenticates as itself with a service token and names the person, and the assistant decides
        // what that person may do from its own replica of the cluster's accounts.
        //
        // Registered unconditionally and inert without a secret — a bot on a machine standing alone mints
        // nothing and starts no worker, which is a deployment fact rather than a misconfiguration. The
        // secret is read through ClusterConfiguration so every member on the machine spells the key
        // identically; one that spelled it differently would read a blank from a file that is not empty
        // and quietly report itself standalone.
        //
        // The store sits inside this bot's own StateDirectory=, because two members sharing one would
        // share a roster and an outbox.
        var clusterSettings = configuration.GetSection(BotClusterOptions.Section)
            .Get<BotClusterOptions>() ?? new BotClusterOptions();
        services.Configure<BotClusterOptions>(configuration.GetSection(BotClusterOptions.Section));

        services.AddKgsmCluster(new ClusterOptions
        {
            // Named per MEMBER, never per host: a machine running a node and an assistant beside this
            // bot holds three members, and they cannot share a name. Blank names it after the machine.
            MemberId = clusterSettings.MemberId is { Length: > 0 } id
                ? id.Trim()
                : Environment.MachineName.Trim().ToLowerInvariant() + "-bot",
            Secret = ClusterConfiguration.Secret(configuration),
            SecretPrevious = ClusterConfiguration.SecretPrevious(configuration),
            StorePath = Path.Combine(StatePaths.DefaultDirectory, "cluster.db"),
            // A chat surface, not a node: it runs no game servers and hosts no leaves.
            Kind = MemberKind.Anchor,
            // Members only. This bot has no browser surface, so the address it states is never one a
            // browser is handed — it exists so the anchor has somewhere to push an account change to.
            GossipUrl = clusterSettings.GossipUrl.Trim(),
        });

        // The chat capability's name, when a DNS anchor holds the cluster's zone: this bot says where it
        // is reached, and while it holds the capability it keeps a certificate for the name and serves it
        // — the member wire, which is all this bot serves. Inert with no cluster and with nobody holding
        // dns.
        services.AddKgsmDnsMember(clusterSettings.PublicHost, "kgsm-bot");

        // Which Discord servers this host announces into. A singleton because it holds the open
        // store, and — like the account store — opening it is what can fail, so it fails into an
        // unavailable store rather than out of the constructor and takes the whole bot with it.
        services.Configure<GuildOptions>(configuration.GetSection(GuildOptions.Section));
        services.AddSingleton<IGuildStore, Guilds.SqliteGuildStore>();

        // The line to the assistant leaf. Registered whether or not this host has one: the client
        // reports itself unconfigured and the conversational surface stays off, which is what
        // "a leaf runs standalone" means for an optional sibling.
        services.AddSingleton<IAssistantTurnClient, Assistant.AssistantTurnClient>();

        // This host's KGSM accounts — the one answer to who may act, shared with the Control Panel
        // and the assistant. A singleton because it holds the open store; opening it is what can
        // fail, and it fails into an unavailable directory rather than out of the constructor.
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddSingleton<KgsmAccounts>();
        services.AddSingleton<IKgsmAccounts>(sp => sp.GetRequiredService<KgsmAccounts>());

        // This member's own copy of the cluster's accounts, and the only thing it resolves a person
        // against. The auth anchor is the single authority in a cluster; every member holds a copy it
        // was given directly and answers from that.
        //
        // A copy of a copy is deliberately not a thing here. This bot forwards a question to the
        // assistant, and the assistant resolves the same person against ITS copy of the same source —
        // two members reading one authority, neither taking the other's word for who somebody is.
        services.AddSingleton<IReplicatedAccounts>(sp => sp.GetRequiredService<KgsmAccounts>());
        services.AddSingleton<IClusterMessageHandler, AccountReplicationHandler>();
        services.AddSingleton<IClusterMessageHandler, AccountRemovalHandler>();

        // Which member of the cluster carries the chat surface. Registered beside the membership
        // above and inert without a secret for the same reason: a bot standing alone reports itself
        // not clustered and serves what it always has. It claims only into an assignment nobody
        // holds, so a cluster that already named a bot is left alone.
        services.AddHostedService<Cluster.BotCapabilityWorker>();

        // The first full copy. The stream alone would leave this member holding only what changed after
        // it joined, resolving everybody who existed before that as a stranger.
        services.AddHostedService<AccountSnapshotWorker>();

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
            // GuildVoiceStates is what makes the gateway report who is in which voice channel, which
            // is how /voice finds the caller and how the bot notices a channel emptying. It is not a
            // privileged intent and costs nothing when the voice surface is off.
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
                | GatewayIntents.MessageContent | GatewayIntents.GuildVoiceStates,
            LogLevel = LogSeverity.Info,

            // Discord refuses a non-stage voice connection from a client that cannot negotiate DAVE,
            // its end-to-end encryption, and answers one that tries with close code 4017. Enabling it
            // needs libdave resolvable at runtime (packaged; installed at /usr/lib/libdave.so) — with
            // the library absent Discord.Net logs that it is unavailable and voice cannot connect,
            // while every other surface carries on untouched.
            EnableVoiceDaveEncryption = true,
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

        // The one path out to Discord for everything the bot says unprompted. Registered before its
        // callers because every one of them takes it: announcements, the status board and channel
        // management are four independent producers of the same scarce thing — rate-limit headroom —
        // and being throttled off the API loses the rest with whichever call spent the last of it.
        // A singleton, and it must be: two of these would be two rates, which is no rate at all.
        services.AddSingleton<IDiscordSendQueue, DiscordSendQueue>();

        // Application service implementations
        services.AddSingleton<IDiscordChannelRegistry, DiscordChannelRegistry>();

        // Looks into a server the supervisor gave up on. A singleton because it owns the one-at-a-time
        // slot and the per-server cooldown: two of these would be two investigations of one crash,
        // racing each other for the same Ollama and posting twice in the same thread.
        services.AddSingleton<IIncidentTriage, IncidentTriage>();
        services.AddSingleton<IDiscordNotificationService, DiscordNotificationService>();

        // The one message per guild that is kept current. A singleton because it owns the publishing
        // loop and the coalescing window — two of these would spend two edits on every change.
        services.AddSingleton<IStatusBoard, StatusBoardService>();

        // Restores proposed and not yet confirmed. A singleton because the handle minted by the
        // command has to be the one the button redeems, and in memory because a destructive action
        // that survives a restart is one somebody can click by accident days later.
        services.AddSingleton<IStagedRestores, StagedRestores>();

        // Said once, when the bot is added to a Discord server nobody has set up. A singleton because
        // it holds the gateway subscription; two would introduce the bot twice in the same guild.
        services.AddSingleton<IGuildGreeter, GuildGreeterService>();

        // The line beside the bot's own name. A singleton for the same reason as the board and more
        // strictly: a gateway presence update is limited per session rather than per caller, so two
        // loops would be two rates against one budget with neither aware of the other.
        services.AddSingleton<IBotPresence, BotPresenceService>();

        // Whether the things the bot depends on are answering. Stateless and asked for by a person,
        // so nothing is held between calls — every check is run at the moment it is reported, which
        // is the only way an answer about right now can be one.
        services.AddSingleton<IBotHealth, BotHealthService>();

        // Listening and speaking in a voice channel, which the package wires itself: most of that
        // graph is factories breaking a cycle, and the reasons live with the code they constrain.
        // What stays here is what only this bot can answer — where the models are, what this host's
        // servers are called, and what a spoken request means.
        services.AddSingleton<global::KGSM.Bot.Infrastructure.Speech.HostSpeech>();
        services.AddSingleton<ISpeechEngine>(sp =>
            sp.GetRequiredService<global::KGSM.Bot.Infrastructure.Speech.HostSpeech>());
        services.AddSingleton<ISpeechToText, global::KGSM.Bot.Infrastructure.Speech.LeafSpeechToText>();
        services.AddSingleton<ITextToSpeech, global::KGSM.Bot.Infrastructure.Speech.LeafTextToSpeech>();

        // The transport half of the voice settings, as the package asks for them. The Control Panel
        // describes the whole of VoiceOptions; only these reach the pipeline.
        services.AddSingleton<IOptions<DiscordVoiceOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<DiscordOptions>>().Value.Voice.ForVoice()));

        services.AddDiscordVoice();

        // IVoiceCommandHandler is registered by the Discord layer (Program.cs) rather than here:
        // answering means posting to Discord and offering the same confirmation buttons the
        // @-mention surface does.

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

        // Read EVERY producer's journal, not the engine's alone. Half of what this bot announces is
        // not the engine's to say: the supervisor owns crashes, give-ups and player presence, and a
        // reader of one journal hears none of them — which from inside Discord is indistinguishable
        // from a host where nothing went wrong. Crash announcements, the incident thread under them
        // and the restart button on a give-up all hang off events this call is what delivers.
        //
        // Must stay AFTER AddKgsmServices: that call registers a single-journal IEventSource and
        // IEventJournalHistory, and this one replaces both by being registered last. Above it, this
        // silently does nothing — there is no error, only the wrong registration winning.
        //
        // Both halves move together, deliberately. /history reads the same record the announcements
        // tail, and a history that could not show the crash somebody was just told about would be
        // the more confusing of the two failures.
        //
        // Tail, no cursor — unchanged from the single-journal reader and for the same reason. The
        // federated source keeps one position per producer, so a cursor here would replay each
        // journal's backlog independently after a restart and announce a morning's crashes at once.
        services.AddKgsmJournalFederation(
            cursorPath: null,
            startPosition: TheKrystalShip.KGSM.Core.Models.EventStartPosition.Tail,
            engineJournalDirectory: kgsmOptions.JournalDir);

        // This leaf's OWN journal — the write half, which it has never had. It reads every other
        // producer's above; this is the one it writes, and it records nothing but what this bot could
        // and could not do. The two are unrelated: reading the federation says nothing about being
        // able to be read, and until now nothing on this host could see a bot that had gone silent.
        // The ENTRY assembly, not this one. The version lives on KGSM.Bot.Discord (the deployable);
        // the class libraries carry none, so passing this assembly stamped every event 1.0.0 — a
        // version no release was ever numbered, which is exactly what ProducerVersion exists to avoid.
        services.AddKgsmJournal(
            "kgsm-bot",
            Assembly.GetEntryAssembly() ?? typeof(DependencyInjection).Assembly);
        services.AddSingleton<LeafLifecycle>();

        // Typed client for the kgsm-watchdog control socket (read-only supervision
        // surface). Registration always succeeds — the socket path defaults to the
        // daemon's own default — and an absent/unreachable daemon is handled at call
        // time, so this is safe even on hosts where the watchdog isn't deployed.
        services.AddKgsmWatchdogClient(kgsmOptions.WatchdogSocketPath);

        // Typed client for the kgsm-firewall authority, used read-only to answer whether a
        // server's ports are actually reachable. Registration always succeeds; the authority is
        // an optional sibling, and an absent one costs that one answer and nothing else.
        services.AddKgsmFirewallClient(kgsmOptions.FirewallSocketPath);

        // Ambient provenance (who/through-what) for the current action — set at an entry point (a slash
        // command, the LLM message handler), read at the kgsm chokepoint so every mutation is attributable.
        // Singleton: the AsyncLocal inside isolates the value per request flow.
        services.AddSingleton<global::KGSM.Bot.Core.Common.IInvocationContext, global::KGSM.Bot.Core.Common.AsyncLocalInvocationContext>();

        // Application service implementations
        services.AddSingleton<IServerEventHandler, KgsmServerEventHandler>();
        services.AddSingleton<IBlueprintService, KgsmBlueprintService>();
        services.AddSingleton<IServerInstanceService, KgsmServerInstanceService>();
        services.AddSingleton<IWatchdogService, WatchdogService>();
        services.AddSingleton<IFirewallReport, FirewallReport>();
        services.AddSingleton<IHostAddressService, HostAddressService>();
        services.AddSingleton<IServerConnectionService, ServerConnectionService>();

        // Who is playing, joined from the engine's run state and the supervisor's live session map.
        // One registration because there must be exactly one place a player count comes from — the
        // command and the status board both read this, and two derivations would be two numbers that
        // can disagree in front of the same person.
        services.AddSingleton<IPlayerRoster, PlayerRoster>();

        // What this host can say about its backups, cached per server and dropped when the engine
        // says a backup was taken or rolled back. One registration for the same reason as the roster:
        // the board and the commands must not be able to print different answers about one server.
        services.AddSingleton<IBackupInsight, BackupInsight>();

        // What this host did, read back off the journal. Nothing is cached and nothing is held: the
        // reader scans only the segments a window can touch, and a question asked at human cadence is
        // cheaper to answer than an index would be to keep in step with the record.
        services.AddSingleton<IServerHistory, ServerHistory>();

        // Cached inventory (avoids spawning kgsm per message)
        services.AddSingleton<IKgsmStateCache, KgsmStateCache>();

        // How a server is written down, for the surfaces that hold an id and nothing else. Reads the
        // cache above, so a label costs no kgsm process.
        services.AddSingleton<IServerLabels, ServerLabels>();

        return services;
    }
}
