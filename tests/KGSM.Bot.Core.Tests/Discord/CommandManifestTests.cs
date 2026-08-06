using Discord.Interactions;
using Discord.WebSocket;

using FluentAssertions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Discord.Llm;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.KGSM.Auth;

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
        services.AddSingleton<PendingEditStore>();
        services.AddSingleton<IOptions<DiscordOptions>>(Options.Create(new DiscordOptions()));
        services.AddSingleton(KgsmRoleMap.Empty);
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

    [Fact]
    public async Task TheManifestListsExactlyTheCommandsTheBotRegisters()
    {
        IReadOnlyList<SlashCommandInfo> registered = await RegisteredAsync();
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        manifest.Commands.Select(c => c.Name).Should().BeEquivalentTo(registered.Select(PathOf));
        manifest.Commands.Select(c => c.Name).Should().BeInAscendingOrder(StringComparer.Ordinal,
            "the file is committed, and reflection order is not stable enough to diff against");
        manifest.Commands.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EveryCommandCarriesTheDescriptionDiscordShows()
    {
        IReadOnlyList<SlashCommandInfo> registered = await RegisteredAsync();
        CommandManifest manifest = CommandManifest.Build(BotAssembly);

        foreach (SlashCommandInfo cmd in registered)
        {
            BotCommand listed = manifest.Commands.Single(c => c.Name == PathOf(cmd));
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
            BotCommand listed = manifest.Commands.Single(c => c.Name == PathOf(cmd));
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

        manifest.Commands.Where(c => c.Mutates).Select(c => c.Name)
            .Should().BeEquivalentTo(["start", "stop", "restart", "install", "uninstall"]);
    }

    /// <summary>
    /// The manifest states what the bot enforces before running a mutating command, and the modules
    /// have to actually enforce it — the panel prints this word and cannot verify it. Every mutating
    /// command carries the operator gate, and it is the same attribute that marks it mutating, so the
    /// two cannot drift apart.
    /// </summary>
    [Fact]
    public void EveryMutatingCommandIsGatedAtTheTierTheManifestClaims()
    {
        CommandManifest.Build(BotAssembly).Gate.Should().Be(KgsmTiers.Operator);

        IEnumerable<MethodInfo> mutating = BotAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IInteractionModuleBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<SlashCommandAttribute>() is not null
                     && m.GetCustomAttribute<MutatingAttribute>() is not null);

        mutating.Should().NotBeEmpty("the bot has commands that act, and a vacuous pass would hide a regression");

        foreach (MethodInfo method in mutating)
        {
            method.GetCustomAttribute<RequireTierAttribute>()!.Minimum
                .Should().Be(KgsmTier.Operator,
                    "{0} changes something, so it must refuse anyone below operator", method.Name);
        }
    }

    /// <summary>
    /// Reading is gated too: every module carrying slash commands requires guild membership, which the
    /// shared role map floors at viewer. Without it a slash command run in a DM — where there is no
    /// member object to read roles from — would list this host's servers to someone who is not in the
    /// guild at all.
    /// </summary>
    /// <remarks>
    /// Scoped to the modules that declare slash commands. <c>ConfirmationModule</c> answers button
    /// clicks rather than commands and authorizes each one itself, because it has to leave the prompt
    /// standing for whoever IS permitted instead of failing the interaction outright.
    /// </remarks>
    [Fact]
    public void EverySlashCommandModuleRequiresAtLeastViewer()
    {
        IEnumerable<Type> modules = BotAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IInteractionModuleBase).IsAssignableFrom(t))
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttribute<SlashCommandAttribute>() is not null));

        modules.Should().NotBeEmpty("a vacuous pass here would leave every command ungated");

        foreach (Type module in modules)
        {
            module.GetCustomAttribute<RequireTierAttribute>().Should().NotBeNull(
                "{0} exposes slash commands, so it must require membership", module.Name);
        }
    }
}
