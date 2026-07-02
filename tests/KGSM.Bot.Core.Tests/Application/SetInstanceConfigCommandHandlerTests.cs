using FluentAssertions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace KGSM.Bot.Tests.Application;

/// <summary>
/// The real execution path for a confirmed config edit on the bot runs through
/// ServerService (ConfirmationModule → server.SetConfigAsync → IServerInstanceService),
/// not the inert IServerOperations adapter. Covers the instance/key/value pass-through
/// and the surfacing of kgsm's denylist refusal.
/// </summary>
public class SetInstanceConfigCommandHandlerTests
{
    private readonly IServerInstanceService _service = Substitute.For<IServerInstanceService>();
    private readonly IKgsmStateCache _stateCache = Substitute.For<IKgsmStateCache>();
    private readonly IWatchdogService _watchdogService = Substitute.For<IWatchdogService>();

    private IServerService Create() =>
        new ServerService(_service, _stateCache, _watchdogService, NullLogger<ServerService>.Instance);

    [Fact]
    public async Task Handle_CallsServiceWithInstanceKeyValue_AndReportsSuccess()
    {
        _service.SetConfigValueAsync("factorio", "executable_arguments", "--foo=bar baz")
            .Returns(Task.FromResult(Result.Success()));

        var result = await Create().SetConfigAsync("factorio", "executable_arguments", "--foo=bar baz");

        result.IsSuccess.Should().BeTrue();
        await _service.Received(1).SetConfigValueAsync("factorio", "executable_arguments", "--foo=bar baz");
    }

    [Fact]
    public async Task Handle_WhenKgsmRefusesKey_SurfacesTheError()
    {
        _service.SetConfigValueAsync("factorio", "name", "evil")
            .Returns(Task.FromResult(Result.Failure("'name' is a protected key and cannot be set with config-set")));

        var result = await Create().SetConfigAsync("factorio", "name", "evil");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("protected key");
    }
}
