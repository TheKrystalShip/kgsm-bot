using FluentAssertions;

using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

using Xunit;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// What a Discord account may do here is what the KGSM account it is connected to says, and nothing
/// else. These run against a real store file rather than a substitute, because the thing worth
/// pinning is that the bot reads the same rows the Control Panel writes — a fake would agree with
/// whatever this code believes the schema is.
/// </summary>
public sealed class KgsmAccountsTests : IDisposable
{
    private const ulong Snowflake = 245717107596197888;

    private readonly string _dir = Directory.CreateTempSubdirectory("kgsm-bot-accounts").FullName;
    private readonly SqliteUserStore _store;
    private readonly KgsmAccounts _accounts;

    public KgsmAccountsTests()
    {
        string path = Path.Combine(_dir, "users.db");
        _store = new SqliteUserStore(new UserStoreOptions { Path = path });
        // Standalone: a host in no cluster reads the accounts on it, and the empty-replica refusal
        // that only a member can be in does not apply. The clustered case has its own tests.
        _accounts = new KgsmAccounts(
            Options.Create(new AuthOptions { UsersDbPath = path }),
            Standalone(),
            NullLogger<KgsmAccounts>.Instance);
    }

    /// <summary>
    /// A member of a cluster that has not been given its copy of the accounts yet cannot identify
    /// anybody, and says so rather than answering that nobody is connected.
    /// </summary>
    /// <remarks>
    /// This is the cold start every member has: the copy is taken from whichever member holds the auth
    /// capability, and until it lands the store is empty. An empty store answers a real person and a
    /// stranger identically, so reading it as "no account" would refuse everybody on the host with the
    /// one message that tells them to go and connect an account they already have.
    /// </remarks>
    [Fact]
    public async Task AMemberWithNoReplicaYetRefusesWithoutDenying()
    {
        string path = Path.Combine(_dir, "empty-replica.db");
        // Opening it is what creates the file, so the store exists and holds nobody.
        _ = new SqliteUserStore(new UserStoreOptions { Path = path });
        KgsmAccounts cold = new(
            Options.Create(new AuthOptions { UsersDbPath = path }), Clustered(),
            NullLogger<KgsmAccounts>.Instance);

        AccountAnswer answer = await cold.ResolveAsync(Snowflake);

        answer.Outcome.Should().Be(AccountOutcome.Unreadable);
        answer.Tier.Should().Be(KgsmTier.None);
        answer.Reason.Should().Contain("auth capability");
    }

    /// <summary>
    /// The same host standing alone reads its own accounts and refuses an unknown person as unknown —
    /// an empty store there is a host nobody has been given an account on, not a copy that has not
    /// arrived.
    /// </summary>
    [Fact]
    public async Task AStandaloneHostWithNoAccountsSaysTheCallerIsNotLinked()
    {
        AccountAnswer answer = await _accounts.ResolveAsync(Snowflake);

        answer.Outcome.Should().Be(AccountOutcome.NotLinked);
    }

    /// <summary>Cluster options carrying a secret — what a member of a cluster holds.</summary>
    private static ClusterOptions Clustered() => Standalone() with { Secret = "cluster-secret-for-tests" };

    /// <summary>Cluster options with no secret — what a bot on a machine standing alone holds.</summary>
    private static ClusterOptions Standalone() => new()
    {
        MemberId = "test-bot",
        Secret = string.Empty,
        StorePath = Path.Combine(Path.GetTempPath(), $"kgsm-bot-accounts-{Guid.NewGuid():N}.db"),
        Kind = MemberKind.Anchor,
    };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<KgsmUser> SeedAsync(
        KgsmTier tier, UserStatus status = UserStatus.Active, ulong snowflake = Snowflake)
    {
        KgsmUser user = new(
            UserIds.NewUserId(), "haru", "Haru", tier, TierSource.Granted, status,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        await _store.CreateAsync(user);
        await _store.AddCredentialAsync(new UserCredential(
            UserIds.NewCredentialId(), user.UserId, CredentialKind.Identity,
            $"discord:{snowflake}", null, "haru#0", DateTimeOffset.UtcNow, null));

        return user;
    }

    [Fact]
    public async Task TheTierIsTheConnectedAccountsTier()
    {
        await SeedAsync(KgsmTier.Operator);

        AccountAnswer answer = await _accounts.ResolveAsync(Snowflake);

        answer.Outcome.Should().Be(AccountOutcome.Ok);
        answer.Tier.Should().Be(KgsmTier.Operator);
        answer.Account.Should().Be("haru");
        answer.Allows(KgsmTier.Operator).Should().BeTrue();
        answer.Allows(KgsmTier.Admin).Should().BeFalse("the ladder is ordered, and operator is below admin");
    }

    /// <summary>
    /// The gate is having an account here, not being in a chat server. A Discord account nobody has
    /// connected proves nothing, and is told exactly that rather than that it lacks permission.
    /// </summary>
    [Fact]
    public async Task ADiscordAccountConnectedToNothingIsAStranger()
    {
        await SeedAsync(KgsmTier.Admin, snowflake: 111111111111111111);

        AccountAnswer answer = await _accounts.ResolveAsync(Snowflake);

        answer.Outcome.Should().Be(AccountOutcome.NotLinked);
        answer.Tier.Should().Be(KgsmTier.None);
        answer.Allows(KgsmTier.Viewer).Should().BeFalse();
        answer.Refusal(KgsmTier.Viewer).Should().Contain("isn't connected to a KGSM account");
    }

    /// <summary>
    /// Disabling somebody in the Control Panel has to reach Discord with no call between the two
    /// services, which it does because both read the one record.
    /// </summary>
    [Fact]
    public async Task ADisabledAccountHoldsNothingAndSaysSo()
    {
        await SeedAsync(KgsmTier.Admin, UserStatus.Disabled);

        AccountAnswer answer = await _accounts.ResolveAsync(Snowflake);

        answer.Outcome.Should().Be(AccountOutcome.Disabled);
        answer.Tier.Should().Be(KgsmTier.None);
        answer.Refusal(KgsmTier.Viewer).Should().Contain("disabled").And.Contain("haru");
    }

    /// <summary>
    /// An account that authenticates and holds nothing is a real state — newly arrived and not
    /// approved — and it gets its own answer, because being told the tier you lack is a riddle when
    /// the tier you hold is none.
    /// </summary>
    [Fact]
    public async Task AnAccountAwaitingApprovalIsToldItIsWaiting()
    {
        await SeedAsync(KgsmTier.None, UserStatus.Pending);

        AccountAnswer answer = await _accounts.ResolveAsync(Snowflake);

        answer.Allows(KgsmTier.Viewer).Should().BeFalse();
        answer.Refusal(KgsmTier.Viewer).Should().Contain("approve");
    }

    /// <summary>
    /// A store that could not be opened refuses, and never claims the caller holds nothing. "We
    /// could not ask" is a different fact from "the answer is no", and reporting the first as the
    /// second demotes an admin in the middle of whatever went wrong.
    /// </summary>
    [Fact]
    public async Task AnUnreadableStoreRefusesWithoutDenying()
    {
        KgsmAccounts broken = new(
            Options.Create(new AuthOptions { UsersDbPath = "/proc/kgsm-cannot-exist/users.db" }),
            Standalone(),
            NullLogger<KgsmAccounts>.Instance);

        broken.Available.Should().BeFalse();
        broken.UnavailableReason.Should().NotBeNullOrWhiteSpace();

        AccountAnswer answer = await broken.ResolveAsync(Snowflake);

        answer.Outcome.Should().Be(AccountOutcome.Unreadable);
        answer.Allows(KgsmTier.Viewer).Should().BeFalse();
        answer.Refusal(KgsmTier.Viewer).Should().Contain("couldn't read");
    }

    /// <summary>
    /// The gate wording names the tier the command needs and the one the caller holds. The panel is
    /// where that is changed, and saying so is the difference between a refusal somebody can act on
    /// and one they can only be annoyed by.
    /// </summary>
    [Fact]
    public async Task TooLowATierNamesBothTiers()
    {
        await SeedAsync(KgsmTier.Viewer);

        AccountAnswer answer = await _accounts.ResolveAsync(Snowflake);

        answer.Allows(KgsmTier.Operator).Should().BeFalse();
        answer.Refusal(KgsmTier.Operator).Should().Contain("operator").And.Contain("viewer");
    }
}
