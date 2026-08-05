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
    /// The manifest states what the bot itself enforces before running a mutating command. Nothing in
    /// the slash modules checks the action role — that gate lives on the natural-language surface and
    /// the confirm buttons — so the manifest says <c>none</c>. This test fails when a gate is added
    /// without the manifest being told, which would leave the panel understating who can act.
    /// </summary>
    [Fact]
    public void TheGateMatchesWhatTheSlashModulesActuallyCheck()
    {
        IEnumerable<Type> modules = BotAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IInteractionModuleBase).IsAssignableFrom(t));

        bool anyPrecondition = modules.Any(m =>
            m.GetCustomAttributes<PreconditionAttribute>().Any() ||
            m.GetMethods().Any(x => x.GetCustomAttributes<PreconditionAttribute>().Any()));

        anyPrecondition.Should().BeFalse("the manifest reports gate 'none'; a precondition would make that a lie");
        CommandManifest.Build(BotAssembly).Gate.Should().Be("none");
    }
}
