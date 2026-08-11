using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The whole-host backup summary, and the distinction it exists to preserve: a server that has never
/// been backed up and a server this host could not ask about are different answers.
/// </summary>
public sealed class BackupInsightTests
{
    private readonly IKgsmStateCache _cache = Substitute.For<IKgsmStateCache>();
    private readonly IServerInstanceService _instances = Substitute.For<IServerInstanceService>();

    private BackupInsight Insight() => new(
        _cache, _instances, Options.Create(new KgsmCacheOptions()), NullLogger<BackupInsight>.Instance);

    private void Inventory(params string[] names) =>
        _cache.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(names.ToDictionary(n => n, n => new Instance { Name = n })
                as IReadOnlyDictionary<string, Instance>);

    private void Backups(string instance, params InstanceBackup[] backups) =>
        _instances.GetBackupsAsync(instance)
            .Returns(Result.Success<IReadOnlyList<InstanceBackup>>(backups));

    private void Unreadable(string instance) =>
        _instances.GetBackupsAsync(instance)
            .Returns(Result.Failure<IReadOnlyList<InstanceBackup>>("kgsm could not be asked"));

    private static InstanceBackup Backup(string id, DateTimeOffset? taken, string? consistency = "cold") =>
        new() { Id = id, CreatedAt = taken, Consistency = consistency };

    /// <summary>
    /// <b>A present key with a null value and an absent key are different answers.</b> The first is a
    /// server that was asked and has no backups; the second is one this host could not ask about, and
    /// rendering them the same would put "never backed up" in front of an operator whose backups are
    /// fine.
    /// </summary>
    [Fact]
    public async Task NeverBackedUpAndCouldNotLookAreDifferentAnswers()
    {
        Inventory("fresh", "unreachable");
        Backups("fresh");
        Unreadable("unreachable");

        IReadOnlyDictionary<string, InstanceBackup?> latest = await Insight().LatestAsync();

        latest.Should().ContainKey("fresh");
        latest["fresh"].Should().BeNull("it was read, and it genuinely has none");
        latest.Should().NotContainKey("unreachable", "it could not be read, which is not an answer about backups");
    }

    [Fact]
    public async Task TheNewestBackupIsTheOneReported()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Inventory("minecraft");
        Backups("minecraft", Backup("new", now.AddHours(-1)), Backup("old", now.AddDays(-9)));

        IReadOnlyDictionary<string, InstanceBackup?> latest = await Insight().LatestAsync();

        latest["minecraft"]!.Id.Should().Be("new");
    }

    /// <summary>
    /// The read is a kgsm process per server, so the second pass must not repeat it — this is the
    /// whole reason the live status message can carry a backup age at all.
    /// </summary>
    [Fact]
    public async Task TheSummaryIsReadOncePerServerAndThenCached()
    {
        Inventory("minecraft");
        Backups("minecraft", Backup("b", DateTimeOffset.UtcNow));

        BackupInsight insight = Insight();
        await insight.LatestAsync();
        await insight.LatestAsync();

        await _instances.Received(1).GetBackupsAsync("minecraft");
    }

    /// <summary>
    /// The engine's backup events are what keep this current; the TTL is only a backstop.
    /// </summary>
    [Fact]
    public async Task InvalidatingMakesTheNextReadFresh()
    {
        Inventory("minecraft");
        Backups("minecraft", Backup("b", DateTimeOffset.UtcNow));

        BackupInsight insight = Insight();
        await insight.LatestAsync();
        insight.Invalidate("minecraft");
        await insight.LatestAsync();

        await _instances.Received(2).GetBackupsAsync("minecraft");
    }

    /// <summary>
    /// A failure is not cached: remembering "could not look" would keep saying it after the reason had
    /// gone away, and re-reading costs one process.
    /// </summary>
    [Fact]
    public async Task AFailedReadIsNotRemembered()
    {
        Inventory("minecraft");
        Unreadable("minecraft");

        BackupInsight insight = Insight();
        await insight.LatestAsync();
        await insight.LatestAsync();

        await _instances.Received(2).GetBackupsAsync("minecraft");
    }

    /// <summary>
    /// An inventory that could not be read is an empty summary, not a host where nothing is backed up.
    /// </summary>
    [Fact]
    public async Task AnUnreadableInventoryAnswersAboutNothing()
    {
        _cache.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, Instance>>(_ => throw new InvalidOperationException("no kgsm"));

        (await Insight().LatestAsync()).Should().BeEmpty();
    }
}
