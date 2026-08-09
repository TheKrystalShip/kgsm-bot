using Discord;
using Discord.Interactions;

using FluentAssertions;

using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TheKrystalShip.KGSM.Auth;

using Xunit;

namespace KGSM.Bot.Core.Tests.Discord;

/// <summary>
/// The gate in front of every slash command. It asks the account store about the Discord account
/// that is typing, and it hands back the whole refusal rather than a fragment — the interaction
/// handler prints what comes out of here verbatim, so a caller whose account is simply not connected
/// must not be told they lack permission.
/// </summary>
public sealed class RequireTierTests
{
    private const ulong Snowflake = 245717107596197888;

    private static (RequireTierAttribute Gate, IInteractionContext Context, IServiceProvider Services)
        Given(AccountAnswer answer, KgsmTier minimum)
    {
        IKgsmAccounts accounts = Substitute.For<IKgsmAccounts>();
        accounts.ResolveAsync(Snowflake, Arg.Any<CancellationToken>()).Returns(answer);

        ServiceCollection services = new();
        services.AddSingleton(accounts);

        IUser user = Substitute.For<IUser>();
        user.Id.Returns(Snowflake);
        IInteractionContext context = Substitute.For<IInteractionContext>();
        context.User.Returns(user);

        return (new RequireTierAttribute(minimum), context, services.BuildServiceProvider());
    }

    private static async Task<PreconditionResult> CheckAsync(AccountAnswer answer, KgsmTier minimum)
    {
        (RequireTierAttribute gate, IInteractionContext context, IServiceProvider services) =
            Given(answer, minimum);
        return await gate.CheckRequirementsAsync(context, Substitute.For<ICommandInfo>(), services);
    }

    [Fact]
    public async Task AnOperatorAccountClearsAnOperatorGate()
    {
        PreconditionResult result = await CheckAsync(
            new AccountAnswer(AccountOutcome.Ok, KgsmTier.Operator, "haru"), KgsmTier.Operator);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AViewerAccountDoesNotClearAnOperatorGate()
    {
        PreconditionResult result = await CheckAsync(
            new AccountAnswer(AccountOutcome.Ok, KgsmTier.Viewer, "haru"), KgsmTier.Operator);

        result.IsSuccess.Should().BeFalse();
        result.ErrorReason.Should().Contain("operator").And.Contain("viewer");
    }

    /// <summary>
    /// §5·b of the internal-users plan: somebody whose Discord account is attached to nothing has to
    /// be told that, and told how to fix it — never handed an opaque permission error, which points
    /// them at an admin who would find nothing wrong with their roles.
    /// </summary>
    [Fact]
    public async Task AnUnconnectedAccountIsToldHowToConnectIt()
    {
        PreconditionResult result = await CheckAsync(
            new AccountAnswer(AccountOutcome.NotLinked, KgsmTier.None), KgsmTier.Viewer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorReason.Should()
            .Contain("isn't connected to a KGSM account")
            .And.Contain("Connected accounts");
    }

    /// <summary>
    /// An unreadable store refuses without asserting anything about the caller. Passing it through as
    /// a denial would tell an admin mid-incident that they are not one.
    /// </summary>
    [Fact]
    public async Task AnUnreadableStoreRefusesAsAnOutage()
    {
        PreconditionResult result = await CheckAsync(
            new AccountAnswer(AccountOutcome.Unreadable, KgsmTier.None, Reason: "disk on fire"),
            KgsmTier.Viewer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorReason.Should().Contain("couldn't read").And.NotContain("permission");
    }
}
