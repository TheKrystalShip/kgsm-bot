using FluentAssertions;

using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.Auth.Users;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// What a Discord account may do here is what the roles of the KGSM account it is connected to grant,
/// at the server a command names, evaluated over the node's replica — and nothing else.
/// </summary>
public sealed class BotAccessTests : IDisposable
{
    private const ulong Snowflake = 245717107596197888;

    private static readonly Dictionary<string, string> Servers = new()
    {
        ["terraria"] = "9f3c",
        ["factorio"] = "77aa",
    };

    private readonly TestAuthority _authority = new();

    public void Dispose() => _authority.Dispose();

    [Fact]
    public async Task AGrantOnTheNodeReachesEveryServerOnIt()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerStart, AccessScope.ForNode(TestAuthority.Node));

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        person.Outcome.Should().Be(AccountStanding.Ok);
        person.Account.Should().Be("haru");
        (await person.AllowsAsync(KgsmActions.ServerStart, "terraria")).Should().BeTrue();
        (await person.AllowsAsync(KgsmActions.ServerStart, "factorio")).Should().BeTrue();
        (await person.AllowsAsync(KgsmActions.ServerStop, "terraria")).Should().BeFalse("only the start was granted");
    }

    [Fact]
    public async Task AGrantOnOneServerReachesThatInstallAlone()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerRead,
            AccessScope.ForInstance(TestAuthority.Node, "terraria", "9f3c"));

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        (await person.AllowsAsync(KgsmActions.ServerRead, "terraria")).Should().BeTrue();
        (await person.AllowsAsync(KgsmActions.ServerRead, "factorio")).Should().BeFalse();
        (await person.FilterAsync(KgsmActions.ServerRead, ["factorio", "terraria"]))
            .Should().Equal("terraria");
        person.AllowsSomewhere(KgsmActions.ServerRead).Should().BeTrue("a list over the host shows them their one server");
        (await person.AllowsAsync(KgsmActions.ServerRead, null)).Should().BeFalse("the node itself was not granted");
    }

    /// <summary>
    /// A reinstall under the same name is a different install. A grant naming the old one must not
    /// reach the new one, and a server whose nonce cannot be read is judged at the node, where an
    /// instance grant does not reach.
    /// </summary>
    [Fact]
    public async Task AGrantOnAnInstallDoesNotReachAReinstallUnderTheSameName()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerRead,
            AccessScope.ForInstance(TestAuthority.Node, "terraria", "0ld0"));

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        (await person.AllowsAsync(KgsmActions.ServerRead, "terraria")).Should().BeFalse();
    }

    [Fact]
    public async Task AGrantOnAnotherNodeReachesNothingHere()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerStart, AccessScope.ForNode("jessie"));

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        (await person.AllowsAsync(KgsmActions.ServerStart, "terraria")).Should().BeFalse();
        person.AllowsSomewhere(KgsmActions.ServerStart).Should().BeFalse();
    }

    /// <summary>
    /// A bot whose node has not named itself can tell only a grant across the whole cluster reaches it.
    /// </summary>
    [Fact]
    public async Task ABotOnAnUnnamedNodeHonoursOnlyClusterGrants()
    {
        string account = _authority.Person(Snowflake);
        _authority.Grant(account, KgsmActions.ServerStart, AccessScope.ForNode(TestAuthority.Node));
        _authority.Grant(account, KgsmActions.ServerStop, AccessScope.Cluster);

        PersonAccess person = await _authority.Access(Servers, node: null).ResolveAsync(Snowflake);

        (await person.AllowsAsync(KgsmActions.ServerStart, "terraria")).Should().BeFalse();
        (await person.AllowsAsync(KgsmActions.ServerStop, "terraria")).Should().BeTrue();
    }

    [Fact]
    public async Task AnOwnerHoldsEverything()
    {
        _authority.Person(Snowflake);
        _authority.Owner("haru");

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        (await person.AllowsAsync(KgsmActions.ServerUninstall, "factorio")).Should().BeTrue();
        (await person.AllowsAsync(BotActions.AnnouncementsManage, null)).Should().BeTrue();
    }

    /// <summary>Talking to the assistant is held by every active person, with no role at all.</summary>
    [Fact]
    public async Task EveryActivePersonMayTalkToTheAssistant()
    {
        _authority.Person(Snowflake);

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        (await person.AllowsAsync(BotActions.AssistantChat, null)).Should().BeTrue();
        (await person.AllowsAsync(KgsmActions.ServerRead, "terraria")).Should().BeFalse();
    }

    /// <summary>
    /// A refusal names the action, so whoever is asked to grant it knows exactly what to grant.
    /// </summary>
    [Fact]
    public async Task ARefusalNamesTheAction()
    {
        _authority.Person(Snowflake);

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);
        AccessDecision decision = await person.DecideAsync(KgsmActions.ServerRestart, "terraria");

        decision.Allowed.Should().BeFalse();
        person.Refusal(KgsmActions.ServerRestart, decision)
            .Should().Contain("kgsm:server.restart").And.Contain("haru");
    }

    /// <summary>
    /// The gate is having an account, not being in a chat server. A Discord account nobody has
    /// connected proves nothing, and is told exactly that rather than that it lacks permission.
    /// </summary>
    [Fact]
    public async Task ADiscordAccountConnectedToNothingIsAStranger()
    {
        _authority.Person(111111111111111111);

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        person.Outcome.Should().Be(AccountStanding.NotLinked);
        (await person.AllowsAsync(BotActions.AssistantChat, null)).Should().BeFalse();
        person.Refusal(KgsmActions.ServerRead).Should().Contain("isn't connected to a KGSM account");
    }

    /// <summary>
    /// Disabling somebody reaches Discord with no call between the anchor and this bot, because the bot
    /// reads the replica the anchor's change lands in.
    /// </summary>
    [Fact]
    public async Task ADisabledAccountHoldsNothingAndSaysSo()
    {
        _authority.Person(Snowflake, status: UserStatus.Disabled);

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        person.Outcome.Should().Be(AccountStanding.Disabled);
        person.Refusal(KgsmActions.ServerRead).Should().Contain("disabled").And.Contain("haru");
    }

    /// <summary>
    /// An account awaiting approval holds nothing, not even a self action, and is told it is waiting
    /// rather than which action it lacks.
    /// </summary>
    [Fact]
    public async Task AnAccountAwaitingApprovalIsToldItIsWaiting()
    {
        _authority.Person(Snowflake, status: UserStatus.Pending);

        PersonAccess person = await _authority.Access(Servers).ResolveAsync(Snowflake);

        person.Outcome.Should().Be(AccountStanding.Pending);
        (await person.AllowsAsync(BotActions.AssistantChat, null)).Should().BeFalse();
        person.Refusal(BotActions.AssistantChat).Should().Contain("waiting to be approved");
    }

    /// <summary>
    /// A replica that cannot be read refuses, and never claims the caller holds nothing. "We could not
    /// ask" is a different fact from "the answer is no", and reporting the first as the second demotes
    /// an Owner in the middle of whatever went wrong.
    /// </summary>
    [Fact]
    public async Task AnUnreadableReplicaRefusesWithoutDenying()
    {
        BotAccess broken = new(
            new MemberAccess(new AuthorityReplicaFile(
                "/proc/kgsm-cannot-exist/users.db", NullLogger<AuthorityReplicaFile>.Instance)),
            new BotStanding(() => TestAuthority.Node, Substitute.For<global::KGSM.Bot.Core.Interfaces.IKgsmStateCache>()));

        broken.Available.Should().BeFalse();
        broken.UnavailableReason.Should().NotBeNullOrWhiteSpace();

        PersonAccess person = await broken.ResolveAsync(Snowflake);

        person.Outcome.Should().Be(AccountStanding.Unreadable);
        person.Refusal(KgsmActions.ServerRead).Should().Contain("couldn't read").And.NotContain("permission");
    }
}
