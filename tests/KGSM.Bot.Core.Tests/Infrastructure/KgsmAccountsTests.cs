using FluentAssertions;

using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

using Xunit;

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
        _accounts = new KgsmAccounts(
            Options.Create(new AuthOptions { UsersDbPath = path }),
            NullLogger<KgsmAccounts>.Instance);
    }

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
