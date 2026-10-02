using Discord.Interactions;
using Discord.WebSocket;

using FluentAssertions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using TheKrystalShip.Discord.Voice;
using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.KGSM;

using System.Reflection;

using Xunit;

namespace KGSM.Bot.Core.Tests.Discord;

/// <summary>
/// The command manifest is what the Control Panel lists, and it is read by a process that never talks
/// to Discord — so the only thing keeping it true is that it agrees with what this bot actually
/// registers. These tests hold it against <see cref="InteractionService"/> itself: the same module
/// scan the bot performs at startup, driven from the same assembly, compared command for command and
/// option for option. A command added, renamed, re-described or given a new option reaches the panel
/// or fails here.
/// </summary>
public sealed class CommandManifestTests
{
    private static readonly Assembly BotAssembly = typeof(InstancesModule).Assembly;

    // The modules' constructor dependencies. Discord.Net instantiates each module while it builds the
    // command table, so the scan needs a container that can satisfy them — nothing here is exercised.
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IServerService>());
        services.AddSingleton(Substitute.For<IKgsmStateCache>());
        services.AddSingleton(Substitute.For<IInvocationContext>());
        services.AddSingleton(Substitute.For<IAssistantTurnClient>());
        services.AddSingleton<IOptions<DiscordOptions>>(Options.Create(new DiscordOptions()));
        services.AddSingleton(Substitute.For<IBotAccess>());
        services.AddSingleton(Substitute.For<IGuildStore>());
        services.AddSingleton(Substitute.For<IStatusBoard>());
        services.AddSingleton(Substitute.For<IServerLabels>());
        services.AddSingleton(Substitute.For<IServerConnectionService>());
        services.AddSingleton(Substitute.For<IPlayerRoster>());
        services.AddSingleton(Substitute.For<IServerInstanceService>());
        services.AddSingleton(Substitute.For<IBackupInsight>());
        services.AddSingleton(Substitute.For<ITextToSpeech>());
        services.AddSingleton(Substitute.For<IStagedRestores>());
        services.AddSingleton(Substitute.For<IServerHistory>());
        services.AddSingleton(Substitute.For<IBotHealth>());
        services.AddSingleton(Substitute.For<IVoiceSessions>());
        services.AddSingleton(Substitute.For<IVoiceTally>());
        return services.BuildServiceProvider();
    }

    // Discord.Net's own view of the commands this assembly declares: the table the bot hands to
    // Discord on Ready.
    private static async Task<IReadOnlyList<SlashCommandInfo>> RegisteredAsync()
    {
        using var client = new DiscordSocketClient();
        using ServiceProvider provider = Services();
        var interactions = new InteractionService(client);
        await interactions.AddModulesAsync(BotAssembly, provider);
        return interactions.SlashCommands;
    }

    // What a user types, including any group word the module nests its commands under.
    private static string PathOf(SlashCommandInfo cmd) =>
        string.IsNullOrEmpty(cmd.Module.SlashGroupName) ? cmd.Name : cmd.Module.SlashGroupName + " " + cmd.Name;

    private static IReadOnlyList<BotCommand> AllCommands(CommandManifest manifest) => manifest.Commands;

    /// <summary>
    /// A command with no action is not listed at all, so listing exactly what Discord registers is also
    /// the proof that every command checks one: nothing the bot answers is open to anybody who can
    /// reach it, including in a DM, since the commands are registered globally.
    /// </summary>
    [Fact]
    public async Task TheManifestListsExactlyTheCommandsTheBotRegisters()
    {
        IReadOnlyList<SlashCommandInfo> registered = await RegisteredAsync();
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        manifest.SchemaVersion.Should().Be(3);
        AllCommands(manifest).Select(c => c.Name).Should().BeEquivalentTo(registered.Select(PathOf));
        AllCommands(manifest).Select(c => c.Name).Should().BeInAscendingOrder(StringComparer.Ordinal,
            "the file is committed, and reflection order is not stable enough to diff against");
        AllCommands(manifest).Should().NotBeEmpty();
    }

    [Fact]
    public async Task EveryCommandCarriesTheDescriptionDiscordShows()
    {
        IReadOnlyList<SlashCommandInfo> registered = await RegisteredAsync();
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        foreach (SlashCommandInfo cmd in registered)
        {
            BotCommand listed = AllCommands(manifest).Single(c => c.Name == PathOf(cmd));
            listed.Description.Should().Be(cmd.Description).And.NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// Each option's name, type, requiredness and autocomplete are Discord's, not a re-description of
    /// them: the panel tells someone what to type, so an option listed as optional that Discord
    /// refuses without is a worse answer than no list at all.
    /// </summary>
    [Fact]
    public async Task EveryOptionMatchesTheOneDiscordWillAskFor()
    {
        IReadOnlyList<SlashCommandInfo> registered = await RegisteredAsync();
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        foreach (SlashCommandInfo cmd in registered)
        {
            BotCommand listed = AllCommands(manifest).Single(c => c.Name == PathOf(cmd));
            listed.Options.Should().HaveCount(cmd.Parameters.Count);

            foreach ((CommandOption option, SlashCommandParameterInfo p) in listed.Options.Zip(cmd.Parameters))
            {
                option.Name.Should().Be(p.Name.ToLowerInvariant());
                option.Description.Should().Be(p.Description);
                option.Required.Should().Be(p.IsRequired);
                option.Autocomplete.Should().Be(p.IsAutocomplete);
                // The manifest's type vocabulary IS Discord's, lowercased — so an option type nothing
                // maps yet fails here rather than shipping under a label that means something else.
                option.Type.Should().Be(p.DiscordOptionType?.ToString().ToLowerInvariant());
            }
        }
    }

    /// <summary>
    /// Which commands change something is a judgement, not something reflection can see, so it is
    /// declared with <c>[Mutating]</c> and pinned here. A new command that acts on a server and is not
    /// in this list is listed to operators as read-only — that is the failure this test exists to
    /// prevent, and the fix is the attribute, not the list.
    /// </summary>
    [Fact]
    public void ExactlyTheCommandsThatActOnAServerAreMarkedAsMutating()
    {
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        AllCommands(manifest).Where(c => c.Mutates).Select(c => c.Name)
            .Should().BeEquivalentTo(
                ["start", "stop", "restart", "install", "uninstall", "backup", "restore"]);
    }

    /// <summary>
    /// The action each command checks, pinned by name. The manifest reads it off the same
    /// <c>RequireAction</c> the precondition runs, so the two cannot drift apart; what this pins is the
    /// decision — a command that starts a server checks the engine's start, the same action the Control
    /// Panel and the assistant check for it, and changing one is a decision somebody made here.
    /// </summary>
    [Fact]
    public void EveryCommandChecksTheActionItPerforms()
    {
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        AllCommands(manifest).ToDictionary(c => c.Name, c => c.Action).Should().BeEquivalentTo(
            new Dictionary<string, string>
            {
                ["start"] = KgsmActions.ServerStart,
                ["stop"] = KgsmActions.ServerStop,
                ["restart"] = KgsmActions.ServerRestart,
                ["status"] = KgsmActions.ServerRead,
                ["supervision"] = KgsmActions.ServerRead,
                ["is-active"] = KgsmActions.ServerRead,
                ["list"] = KgsmActions.ServerRead,
                ["connect"] = KgsmActions.ServerRead,
                ["players"] = KgsmActions.ServerRead,
                ["history"] = KgsmActions.ServerRead,
                ["install"] = KgsmActions.ServerInstall,
                ["uninstall"] = KgsmActions.ServerUninstall,
                ["backups"] = KgsmActions.ServerBackupsRead,
                ["backup"] = KgsmActions.ServerBackupsCreate,
                ["restore"] = KgsmActions.ServerBackupsRestore,
                // A server's log carries the network address of everyone who connected: it is the
                // console's read, not the read of whether a server is up.
                ["logs"] = KgsmActions.ServerConsoleRead,
                ["ping"] = BotActions.StatusRead,
                ["about"] = BotActions.StatusRead,
                ["health"] = BotActions.StatusRead,
                // Where this host broadcasts is a setting of the bot's own, granted like any other.
                ["setup show"] = BotActions.AnnouncementsManage,
                ["setup announce"] = BotActions.AnnouncementsManage,
                ["setup board"] = BotActions.AnnouncementsManage,
                ["setup board-off"] = BotActions.AnnouncementsManage,
                ["setup status"] = BotActions.AnnouncementsManage,
                ["setup status-off"] = BotActions.AnnouncementsManage,
                ["setup follow"] = BotActions.AnnouncementsManage,
                ["setup unfollow"] = BotActions.AnnouncementsManage,
                ["setup follow-all"] = BotActions.AnnouncementsManage,
                ["setup forget"] = BotActions.AnnouncementsManage,
                // A bot sitting in a voice channel hears everybody in the room, including people who
                // never addressed it — an action of its own, though it changes no server.
                ["voice join"] = BotActions.VoiceUse,
                ["voice leave"] = BotActions.VoiceUse,
                ["voice speak-as"] = BotActions.VoiceUse,
                ["voice status"] = BotActions.VoiceUse,
                // The floor; the assistant checks clearing a conversation a thread shares itself.
                ["conversation clear"] = BotActions.AssistantChat,
                ["conversation compact"] = BotActions.AssistantChat,
            });
    }

    /// <summary>
    /// A command that acts on one server names the option the server is given in, so it is judged at
    /// that server; the option has to be a real string parameter of the command, or every check would
    /// fall back to the whole host.
    /// </summary>
    [Fact]
    public void EveryServerOptionAGateNamesIsOneTheCommandTakes()
    {
        IEnumerable<MethodInfo> commands = BotAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IInteractionModuleBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<SlashCommandAttribute>() is not null);

        foreach (MethodInfo method in commands)
        {
            if (method.GetCustomAttribute<RequireActionAttribute>() is not { Server: { } server })
                continue;

            method.GetParameters().Should().Contain(
                p => p.Name == server && p.ParameterType == typeof(string),
                "{0} is judged at the server its '{1}' option names", method.Name, server);
        }

        commands.Where(m => m.GetCustomAttribute<RequireActionAttribute>() is { Server: not null })
            .Should().NotBeEmpty("a vacuous pass would hide every server-scoped gate falling back to the host");
    }

    /// <summary>A command that changes something never checks only a read.</summary>
    [Fact]
    public void NoActingCommandChecksOnlyARead()
    {
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        AllCommands(manifest).Where(c => c.Mutates)
            .Should().OnlyContain(c => !c.Action.EndsWith(".read", StringComparison.Ordinal));
    }
}
