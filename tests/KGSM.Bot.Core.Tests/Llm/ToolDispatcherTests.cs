using FluentAssertions;

using KGSM.Bot.Application.Commands;
using KGSM.Bot.Application.Queries;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Llm;

using MediatR;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace KGSM.Bot.Tests.Llm;

public class ToolDispatcherTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IKgsmStateCache _cache = Substitute.For<IKgsmStateCache>();
    private readonly ConfirmationContext _confirmations = new();

    public ToolDispatcherTests()
    {
        // Two terraria-* instances (matched by substring) plus a unique minecraft.
        var instances = new Dictionary<string, Instance>
        {
            ["terraria-pvp"] = new Instance { Name = "terraria-pvp" },
            ["terraria-creative"] = new Instance { Name = "terraria-creative" },
            ["minecraft"] = new Instance { Name = "minecraft" },
        };
        _cache.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, Instance>)instances);

        var blueprints = new Dictionary<string, Blueprint>
        {
            ["valheim"] = new Blueprint { Name = "valheim" },
            ["terraria"] = new Blueprint { Name = "terraria" },
        };
        _cache.GetBlueprintsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, Blueprint>)blueprints);
    }

    private ToolDispatcher Create() =>
        new(_mediator, _cache, _confirmations, NullLogger<ToolDispatcher>.Instance);

    private static LlmToolCall Call(string name, string instance) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = instance });

    private static LlmToolCall InstallCall(string blueprint, string? name = null) =>
        new(LlmTools.InstallServer, new Dictionary<string, string?>
        {
            ["blueprint_name"] = blueprint,
            ["instance_name"] = name,
        });

    [Fact]
    public async Task ExactName_Resolves_AndExecutes()
    {
        _mediator.Send(Arg.Any<IsServerActiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(ServerActiveResult.Success(true));

        var result = await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "minecraft"));

        result.Should().Contain("running");
        await _mediator.Received(1).Send(
            Arg.Is<IsServerActiveQuery>(q => q.InstanceName == "minecraft"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleFuzzyMatch_Resolves()
    {
        _mediator.Send(Arg.Any<IsServerActiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(ServerActiveResult.Success(false));

        // "pvp" is a substring of exactly one instance.
        await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "pvp"));

        await _mediator.Received(1).Send(
            Arg.Is<IsServerActiveQuery>(q => q.InstanceName == "terraria-pvp"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousName_AsksUser_AndDoesNotExecute()
    {
        // "terraria" matches two instances by game type / substring.
        var result = await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "terraria"));

        result.Should().Contain("Ambiguous")
            .And.Contain("terraria-pvp")
            .And.Contain("terraria-creative");
        await _mediator.DidNotReceive().Send(Arg.Any<IsServerActiveQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownName_ReturnsMiss_WithKnownList()
    {
        var result = await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "doesnotexist"));

        result.Should().Contain("no instance named").And.Contain("minecraft");
        await _mediator.DidNotReceive().Send(Arg.Any<IsServerActiveQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownTool_IsRefused()
    {
        var result = await Create().ExecuteAsync(
            new LlmToolCall("delete_everything", new Dictionary<string, string?>()));

        result.Should().Contain("not a known tool");
    }

    [Fact]
    public async Task UninstallServer_StagesConfirmation_AndDoesNotExecute()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(LlmTools.UninstallServer, "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(ConfirmationKind.Uninstall, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");
        await _mediator.DidNotReceive().Send(Arg.Any<UninstallServerCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallServer_AmbiguousTarget_DoesNotStage()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(LlmTools.UninstallServer, "terraria"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("Ambiguous");
    }

    [Fact]
    public async Task InstallServer_ResolvesBlueprint_AndStagesConfirmation()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Create().ExecuteAsync(InstallCall("valheim", "my-valheim"));

            result.Should().Contain("Staged");
            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new PendingConfirmation(ConfirmationKind.Install, "valheim", "my-valheim"));
        }
        await _mediator.DidNotReceive().Send(Arg.Any<InstallServerCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallServer_NameCollision_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Create().ExecuteAsync(InstallCall("valheim", "minecraft"));

            result.Should().Contain("already exists");
            _confirmations.Staged.Should().BeEmpty();
        }
    }
}
