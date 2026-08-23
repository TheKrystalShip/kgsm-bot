using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

// KGSM.Lib carries its own KgsmOptions; pin the unqualified name to the bot's, as the service does.
using KgsmOptions = KGSM.Bot.Infrastructure.Configuration.KgsmOptions;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The engine's exit code is the verdict on a mutating call, and this is the seam that turns it into
/// something Discord shows somebody.
/// </summary>
/// <remarks>
/// kgsm refuses an install or an uninstall it cannot make — an unknown or offline library, a name
/// already taken, a full disk, a server still running — and writes the reason to stderr. A surface
/// that discards the result announces every one of those as a completed action, which is the
/// fabricated status the ecosystem forbids. These pin that the engine's own words come back instead.
/// </remarks>
public sealed class KgsmServerInstanceServiceTests
{
    private readonly IInstanceService _instances = Substitute.For<IInstanceService>();
    private readonly IKgsmClient _client = Substitute.For<IKgsmClient>();

    public KgsmServerInstanceServiceTests()
    {
        _client.Instances.Returns(_instances);
    }

    private KgsmServerInstanceService Create() => new(
        _client,
        Options.Create(new KgsmOptions()),
        new AsyncLocalInvocationContext(),
        NullLogger<KgsmServerInstanceService>.Instance);

    [Fact]
    public async Task InstallReportsTheEngineRefusalAndItsReason()
    {
        _instances
            .Install("factorio", "cold-storage", null, null, null, null)
            .Returns(new KgsmResult(55, string.Empty, "Library 'cold-storage' is offline"));

        Core.Common.Result result = await Create().InstallAsync("factorio", "cold-storage");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Library 'cold-storage' is offline");
    }

    /// <summary>
    /// A refusal with nothing on stderr still has to read as a refusal. The stand-in says only that
    /// the reason is unknown — it never invents one.
    /// </summary>
    [Fact]
    public async Task InstallRefusedWithNoStderrStillFailsWithoutInventingAReason()
    {
        _instances
            .Install("factorio", null, null, null, null, null)
            .Returns(new KgsmResult(56));

        Core.Common.Result result = await Create().InstallAsync("factorio");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Unknown error");
    }

    [Fact]
    public async Task InstallSucceedsOnAZeroExit()
    {
        _instances
            .Install("factorio", null, null, null, null, null)
            .Returns(new KgsmResult(0));

        (await Create().InstallAsync("factorio")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UninstallReportsTheEngineRefusalAndItsReason()
    {
        _instances
            .Uninstall("factorio", null, null)
            .Returns(new KgsmResult(57, string.Empty, "Instance 'factorio' is running"));

        Core.Common.Result result = await Create().UninstallAsync("factorio");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Instance 'factorio' is running");
    }

    [Fact]
    public async Task UninstallSucceedsOnAZeroExit()
    {
        _instances
            .Uninstall("factorio", null, null)
            .Returns(new KgsmResult(0));

        (await Create().UninstallAsync("factorio")).IsSuccess.Should().BeTrue();
    }
}
