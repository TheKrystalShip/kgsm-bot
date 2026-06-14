using FluentAssertions;

using KGSM.Bot.Application.Commands;
using KGSM.Bot.Application.Handlers;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace KGSM.Bot.Tests.Application;

/// <summary>
/// The real execution path for a confirmed config edit on the bot runs through this
/// handler (ConfirmationModule → mediator → here → IServerInstanceService), not the
/// inert IServerOperations adapter. Covers the instance/key/value pass-through and the
/// surfacing of kgsm's denylist refusal.
/// </summary>
public class SetInstanceConfigCommandHandlerTests
{
    private readonly IServerInstanceService _service = Substitute.For<IServerInstanceService>();

    private SetInstanceConfigCommandHandler Create() =>
        new(_service, NullLogger<SetInstanceConfigCommandHandler>.Instance);

    [Fact]
    public async Task Handle_CallsServiceWithInstanceKeyValue_AndReportsSuccess()
    {
        _service.SetConfigValueAsync("factorio", "executable_arguments", "--foo=bar baz")
            .Returns(Task.FromResult(Result.Success()));

        var result = await Create().Handle(
            new SetInstanceConfigCommand("factorio", "executable_arguments", "--foo=bar baz"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _service.Received(1).SetConfigValueAsync("factorio", "executable_arguments", "--foo=bar baz");
    }

    [Fact]
    public async Task Handle_WhenKgsmRefusesKey_SurfacesTheError()
    {
        _service.SetConfigValueAsync("factorio", "name", "evil")
            .Returns(Task.FromResult(Result.Failure("'name' is a protected key and cannot be set with config-set")));

        var result = await Create().Handle(
            new SetInstanceConfigCommand("factorio", "name", "evil"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("protected key");
    }
}
