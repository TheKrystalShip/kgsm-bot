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

namespace KGSM.Bot.Tests.Infrastructure;

public class KgsmStateCacheTests
{
    private readonly IServerInstanceService _instances = Substitute.For<IServerInstanceService>();
    private readonly IBlueprintService _blueprints = Substitute.For<IBlueprintService>();

    private KgsmStateCache Create() => new(
        _instances, _blueprints,
        Options.Create(new KgsmCacheOptions { InstancesTtlSeconds = 300, BlueprintsTtlSeconds = 600 }),
        NullLogger<KgsmStateCache>.Instance);

    private static Result<IReadOnlyDictionary<string, Instance>> Inst(params string[] names) =>
        Result.Success<IReadOnlyDictionary<string, Instance>>(
            names.ToDictionary(n => n, n => new Instance { Name = n }));

    [Fact]
    public async Task SecondCallWithinTtl_ServesFromCache_WithoutRefetching()
    {
        _instances.GetAllAsync().Returns(Inst("a"));
        var cache = Create();

        var first = await cache.GetInstancesAsync();
        var second = await cache.GetInstancesAsync();

        first.Should().HaveCount(1);
        second.Should().HaveCount(1);
        await _instances.Received(1).GetAllAsync();
    }

    [Fact]
    public async Task Invalidate_ForcesRefetch_OnNextCall()
    {
        _instances.GetAllAsync().Returns(Inst("a"), Inst("a", "b"));
        var cache = Create();

        var before = await cache.GetInstancesAsync();
        before.Should().HaveCount(1);

        cache.InvalidateInstances();
        var after = await cache.GetInstancesAsync();

        after.Should().HaveCount(2);
        await _instances.Received(2).GetAllAsync();
    }

    [Fact]
    public async Task RefreshFailure_KeepsLastKnownGoodSnapshot()
    {
        _instances.GetAllAsync().Returns(
            Inst("a"),
            Result.Failure<IReadOnlyDictionary<string, Instance>>("kgsm exploded"));
        var cache = Create();

        var good = await cache.GetInstancesAsync();
        good.Should().HaveCount(1);

        cache.InvalidateInstances();
        var afterFailure = await cache.GetInstancesAsync();

        // The failed refresh must not blank the cache.
        afterFailure.Should().HaveCount(1);
    }
}
