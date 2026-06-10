using KGSM.Bot.Application.Commands;
using KGSM.Bot.Application.Queries;

using MediatR;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// Adapts the bot's MediatR command/query handlers to the assistant's
/// <see cref="IServerOperations"/> port. This keeps LLM-initiated actions on the
/// exact same path as the Discord slash commands
/// (MediatR → handler → <c>IServerInstanceService</c>), so behaviour is identical
/// regardless of which front end triggered the action.
/// </summary>
internal sealed class MediatorServerOperations : IServerOperations
{
    private readonly IMediator _mediator;

    public MediatorServerOperations(IMediator mediator) => _mediator = mediator;

    public async Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new StartServerCommand(instance), cancellationToken));

    public async Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new StopServerCommand(instance), cancellationToken));

    public async Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new RestartServerCommand(instance), cancellationToken));

    public async Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new CreateBackupCommand(instance), cancellationToken));

    public async Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new UpdateServerCommand(instance), cancellationToken));

    // install / uninstall are part of the port for the shared confirm path
    // (ServerAssistant.ConfirmAsync). The bot does NOT route confirmations through
    // ConfirmAsync today — ConfirmationModule executes them directly — so these are
    // currently inert here; they exist to satisfy the port and keep the adapter honest.
    public async Task<Result> InstallAsync(string blueprint, string? instanceName, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new InstallServerCommand(blueprint, null, null, instanceName), cancellationToken));

    public async Task<Result> UninstallAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new UninstallServerCommand(instance), cancellationToken));

    public async Task<Result<string>> GetStatusAsync(string instance, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new GetServerStatusQuery(instance), cancellationToken);
        return result.IsSuccess
            ? Result.Success(result.Status ?? string.Empty)
            : Result.Failure<string>(result.ErrorMessage ?? "unknown error");
    }

    public async Task<Result<bool>> IsActiveAsync(string instance, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new IsServerActiveQuery(instance), cancellationToken);
        return result.IsSuccess
            ? Result.Success(result.IsActive)
            : Result.Failure<bool>(result.ErrorMessage ?? "unknown error");
    }

    private static Result Map(OperationResult result) =>
        result.IsSuccess ? Result.Success() : Result.Failure(result.ErrorMessage ?? "unknown error");
}
