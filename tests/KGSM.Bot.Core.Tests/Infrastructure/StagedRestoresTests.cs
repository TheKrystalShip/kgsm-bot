using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The gate in front of the one thing this bot does that cannot be undone.
/// </summary>
/// <remarks>
/// A restore replaces a server's current data, and this holds the proposal between somebody asking
/// for it and somebody confirming it. Everything here is a property that stops a click doing more
/// than the person clicking meant.
/// </remarks>
public sealed class StagedRestoresTests
{
    private static StagedRestores Restores() => new(NullLogger<StagedRestores>.Instance);

    [Fact]
    public void AStagedRestoreIsRedeemedByItsHandle()
    {
        StagedRestores restores = Restores();

        string handle = restores.Stage("minecraft", "minecraft-2026", 42);

        StagedRestore? redeemed = restores.Redeem(handle);

        redeemed.Should().NotBeNull();
        redeemed!.InstanceName.Should().Be("minecraft");
        redeemed.BackupId.Should().Be("minecraft-2026");
        redeemed.ProposedToDiscordUserId.Should().Be(42ul);
    }

    /// <summary>
    /// <b>Once, and only once.</b> A confirmation that can be clicked twice is a restore that runs
    /// twice, and the second one rolls back whatever the first produced.
    /// </summary>
    [Fact]
    public void RedeemingTwiceGetsNothingTheSecondTime()
    {
        StagedRestores restores = Restores();
        string handle = restores.Stage("minecraft", "minecraft-2026", 42);

        restores.Redeem(handle).Should().NotBeNull();
        restores.Redeem(handle).Should().BeNull();
    }

    /// <summary>
    /// Peeking is what lets a click that turns out not to be allowed leave the proposal standing for
    /// the person who is actually entitled to answer it.
    /// </summary>
    [Fact]
    public void PeekingDoesNotConsumeTheProposal()
    {
        StagedRestores restores = Restores();
        string handle = restores.Stage("minecraft", "minecraft-2026", 42);

        restores.Peek(handle).Should().NotBeNull();
        restores.Peek(handle).Should().NotBeNull();
        restores.Redeem(handle).Should().NotBeNull("peeking must leave it redeemable");
    }

    [Fact]
    public void CancellingLeavesNothingToRedeem()
    {
        StagedRestores restores = Restores();
        string handle = restores.Stage("minecraft", "minecraft-2026", 42);

        restores.Cancel(handle);

        restores.Peek(handle).Should().BeNull();
        restores.Redeem(handle).Should().BeNull();
    }

    [Fact]
    public void AHandleNobodyStagedIsNotAccepted()
    {
        StagedRestores restores = Restores();

        restores.Redeem("deadbeef").Should().BeNull();
        restores.Peek("").Should().BeNull();
    }

    /// <summary>
    /// Two proposals must not be able to answer for each other, and the handle must not be guessable —
    /// it is the only thing between a customId somebody can type and a destructive action.
    /// </summary>
    [Fact]
    public void EveryHandleIsDistinctAndUnguessable()
    {
        StagedRestores restores = Restores();

        string[] handles = [.. Enumerable.Range(0, 50).Select(i => restores.Stage("minecraft", $"b{i}", 42))];

        handles.Should().OnlyHaveUniqueItems();
        handles.Should().OnlyContain(h => h.Length == 32 && h.All(Uri.IsHexDigit));
    }

    /// <summary>
    /// The handle has to survive the trip through a Discord component id, which caps at 100 characters
    /// — and a truncated one names a different archive rather than failing.
    /// </summary>
    [Fact]
    public void AHandleFitsAButton()
    {
        string handle = Restores().Stage("a-server-with-a-very-long-name-indeed", "an-equally-long-backup-id", 42);

        RestoreActionIds.Fits(handle).Should().BeTrue();
        RestoreActionIds.Confirm(handle).Length.Should().BeLessThanOrEqualTo(RestoreActionIds.MaxCustomIdLength);
    }

    /// <summary>
    /// The confirm and cancel ids must not share a prefix: the confirm handler matches on a wildcard,
    /// and a cancel id underneath it would be captured and read as a handle.
    /// </summary>
    [Fact]
    public void ConfirmAndCancelCannotBeMistakenForEachOther()
    {
        string handle = Restores().Stage("minecraft", "minecraft-2026", 42);

        RestoreActionIds.Cancel(handle).Should().NotStartWith(RestoreActionIds.ConfirmPrefix);
        RestoreActionIds.Confirm(handle).Should().NotStartWith(RestoreActionIds.CancelPrefix);
    }
}
