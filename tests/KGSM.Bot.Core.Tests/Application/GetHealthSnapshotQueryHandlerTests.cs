using FluentAssertions;

using KGSM.Bot.Application.Handlers;
using KGSM.Bot.Application.Queries;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Tests.Application;

/// <summary>
/// The bot's half of run_health_check: this handler fetches the structured status + host
/// disk and maps them into the neutral <c>InstanceHealthSnapshot</c> the assistant
/// aggregator consumes (fetch + map only — no health judgment here). Covers the mapping,
/// the honest host-disk-unavailable path (no fabricated value), and status failure.
/// </summary>
public class GetHealthSnapshotQueryHandlerTests
{
    private readonly IServerInstanceService _service = Substitute.For<IServerInstanceService>();

    private GetHealthSnapshotQueryHandler Create() =>
        new(_service, NullLogger<GetHealthSnapshotQueryHandler>.Instance);

    [Fact]
    public async Task Handle_MapsStatusLogsVersionAndDisk()
    {
        _service.GetRuntimeStatusAsync("minecraft").Returns(Task.FromResult(Result.Success(
            new InstanceRuntimeStatus
            {
                InstanceName = "minecraft",
                Status = true,
                Version = new VersionInfo
                {
                    Current = "1.20.1",
                    Latest = "1.20.4",
                    Checked = true,
                    UpdatesAvailable = true,
                },
                RecentLogs = "INFO started\nERROR boom\n",
            })));
        _service.GetSystemInfoAsync().Returns(Task.FromResult(Result.Success(new SystemInfo
        {
            Disk = new DiskInfo { UsePercent = "26%", Size = "916G", Available = "649G" },
        })));

        var result = await Create().Handle(new GetHealthSnapshotQuery("minecraft"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var s = result.Snapshot!;
        s.Running.Should().BeTrue();
        s.RecentLogLines.Should().BeEquivalentTo(new[] { "INFO started", "ERROR boom" });
        s.UpdatesAvailable.Should().BeTrue();
        s.CurrentVersion.Should().Be("1.20.1");
        s.LatestVersion.Should().Be("1.20.4");
        s.HostDisk!.UsedPercent.Should().Be(26);
        s.HostDiskUnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task Handle_HostDiskUnavailable_SetsReason_NeverFabricates()
    {
        _service.GetRuntimeStatusAsync("minecraft").Returns(Task.FromResult(Result.Success(
            new InstanceRuntimeStatus
            {
                InstanceName = "minecraft",
                Status = true,
                Version = new VersionInfo { Current = "1.0.0", Checked = false, UpdatesAvailable = null },
                RecentLogs = "",
            })));
        _service.GetSystemInfoAsync()
            .Returns(Task.FromResult(Result.Failure<SystemInfo>("host system info was unavailable")));

        var result = await Create().Handle(new GetHealthSnapshotQuery("minecraft"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var s = result.Snapshot!;
        s.HostDisk.Should().BeNull();                          // no fabricated 0%
        s.HostDiskUnavailableReason.Should().NotBeNullOrEmpty();
        s.UpdatesAvailable.Should().BeNull();                  // honest unknown preserved
    }

    [Fact]
    public async Task Handle_StatusUnreadable_Fails()
    {
        _service.GetRuntimeStatusAsync("ghost")
            .Returns(Task.FromResult(Result.Failure<InstanceRuntimeStatus>("not found")));

        var result = await Create().Handle(new GetHealthSnapshotQuery("ghost"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }
}
