using Discord;
using Discord.Interactions;

using FluentAssertions;

using KGSM.Bot.Core.Tests.Infrastructure;
using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.Auth.Access;

using Xunit;

namespace KGSM.Bot.Core.Tests.Discord;

/// <summary>
/// The gate in front of every slash command. It evaluates the Discord account that is typing for the
/// command's action at the server the command names, and it hands back the whole refusal rather than a
/// fragment — the interaction handler prints what comes out of here verbatim, so a caller whose account
/// is simply not connected must not be told they lack permission.
/// </summary>
public sealed class RequireActionTests : IDisposable
{
    private const ulong Snowflake = 245717107596197888;

    private static readonly Dictionary<string, string> Servers = new()
    {
        ["terraria"] = "9f3c",
        ["factorio"] = "77aa",
    };

    private readonly TestAuthority _authority = new();

    public void Dispose() => _authority.Dispose();

    private async Task<PreconditionResult> CheckAsync(RequireActionAttribute gate, params (string Name, string Value)[] options)
    {
        ServiceCollection services = new();
        services.AddSingleton(_authority.Access(Servers));

        IUser user = Substitute.For<IUser>();
        user.Id.Returns(Snowflake);

        // Built before it is handed over: NSubstitute cannot configure one substitute while another's
        // return value is being set.
        IApplicationCommandInteractionDataOption[] given = [.. options.Select(Option)];
        var data = Substitute.For<IApplicationCommandInteractionData>();
        data.Options.Returns(given);
        var slash = Substitute.For<ISlashCommandInteraction>();
        slash.Data.Returns(data);

        IInteractionContext context = Substitute.For<IInteractionContext>();
        context.User.Returns(user);
        context.Interaction.Returns(slash);

        return await gate.CheckRequirementsAsync(context, Substitute.For<ICommandInfo>(), services.BuildServiceProvider());
    }

    private static IApplicationCommandInteractionDataOption Option((string Name, string Value) given)
    {
        var option = Substitute.For<IApplicationCommandInteractionDataOption>();
        option.Name.Returns(given.Name);
        option.Value.Returns(given.Value);
        option.Type.Returns(ApplicationCommandOptionType.String);
        return option;
    }

    [Fact]
    public async Task TheActionAtTheNamedServerClearsTheGate()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerStart,
            AccessScope.ForInstance(TestAuthority.Node, "terraria", "9f3c"));

        PreconditionResult result = await CheckAsync(
            new RequireActionAttribute(KgsmActions.ServerStart) { Server = "instance" }, ("instance", "terraria"));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task TheActionAtAnotherServerDoesNot()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerStart,
            AccessScope.ForInstance(TestAuthority.Node, "terraria", "9f3c"));

        PreconditionResult result = await CheckAsync(
            new RequireActionAttribute(KgsmActions.ServerStart) { Server = "instance" }, ("instance", "factorio"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorReason.Should().Contain("kgsm:server.start");
    }

    /// <summary>
    /// A command over the whole host — its server left out — admits somebody holding the action at any
    /// server here, and then shows them only those.
    /// </summary>
    [Fact]
    public async Task LeavingTheServerOutNeedsTheActionSomewhereHere()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerRead,
            AccessScope.ForInstance(TestAuthority.Node, "terraria", "9f3c"));

        (await CheckAsync(new RequireActionAttribute(KgsmActions.ServerRead) { Server = "instance" }))
            .IsSuccess.Should().BeTrue();
        (await CheckAsync(new RequireActionAttribute(KgsmActions.ServerRead) { AnyServer = true }))
            .IsSuccess.Should().BeTrue();
        (await CheckAsync(new RequireActionAttribute(KgsmActions.ServerStop) { AnyServer = true }))
            .IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ANodeActionIsJudgedAtThisNode()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, BotActions.VoiceUse, AccessScope.ForNode("jessie"));

        (await CheckAsync(new RequireActionAttribute(BotActions.VoiceUse))).IsSuccess.Should().BeFalse();

        _authority.Grant(account, BotActions.VoiceUse, AccessScope.ForNode(TestAuthority.Node));

        (await CheckAsync(new RequireActionAttribute(BotActions.VoiceUse))).IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Somebody whose Discord account is attached to nothing has to be told that, and told how to fix
    /// it — never handed an opaque permission error, which points them at whoever manages roles, who
    /// would find nothing wrong with theirs.
    /// </summary>
    [Fact]
    public async Task AnUnconnectedAccountIsToldHowToConnectIt()
    {
        PreconditionResult result = await CheckAsync(
            new RequireActionAttribute(KgsmActions.ServerRead) { Server = "instance" }, ("instance", "terraria"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorReason.Should()
            .Contain("isn't connected to a KGSM account")
            .And.Contain("Connected accounts");
    }
}
