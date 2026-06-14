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

    // Like install/uninstall above, inert on the bot today (ConfirmationModule executes the
    // confirmed edit directly via the same SetInstanceConfigCommand); present to satisfy the port.
    public async Task<Result> SetInstanceConfigValueAsync(
        string instance, string key, string value, CancellationToken cancellationToken = default) =>
        Map(await _mediator.Send(new SetInstanceConfigCommand(instance, key, value), cancellationToken));

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

    /// <summary>
    /// Fleet status for the shared <c>get_status</c> (no instance) tool. The fan-out
    /// over instances happens here in C# — it is ONE model-facing tool call, so it
    /// doesn't hit the agent-loop iteration cap the way a per-instance tool loop did.
    /// A per-instance liveness failure becomes an <see cref="FleetStatusAvailability.Unavailable"/>
    /// entry, never a fabricated "stopped". (A future bulk MediatR query could replace
    /// the loop; reused the existing per-instance query here to avoid new bot handlers.)
    /// </summary>
    public async Task<Result<IReadOnlyList<FleetStatusEntry>>> GetFleetStatusAsync(CancellationToken cancellationToken = default)
    {
        var all = await _mediator.Send(new GetAllInstancesQuery(), cancellationToken);
        if (!all.IsSuccess || all.Instances is null)
            return Result.Failure<IReadOnlyList<FleetStatusEntry>>(all.ErrorMessage ?? "could not list instances");

        var entries = new List<FleetStatusEntry>(all.Instances.Count);
        foreach (var name in all.Instances.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var active = await _mediator.Send(new IsServerActiveQuery(name), cancellationToken);
            entries.Add(active.IsSuccess
                ? new FleetStatusEntry(name, FleetStatusAvailability.Read, active.IsActive, Reason: null)
                : new FleetStatusEntry(name, FleetStatusAvailability.Unavailable, Running: null,
                    active.ErrorMessage ?? "status could not be read"));
        }

        return Result.Success<IReadOnlyList<FleetStatusEntry>>(entries);
    }

    /// <summary>
    /// Health snapshot for the assistant's <c>run_health_check</c> aggregator — routed
    /// through MediatR like every other read, so Discord narrates real health. The
    /// neutral mapping happens in the query handler; the aggregator's synthesis runs in
    /// the assistant library.
    /// </summary>
    public async Task<Result<InstanceHealthSnapshot>> GetHealthSnapshotAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new GetHealthSnapshotQuery(instance), cancellationToken);
        return result.IsSuccess && result.Snapshot is not null
            ? Result.Success(result.Snapshot)
            : Result.Failure<InstanceHealthSnapshot>(result.ErrorMessage ?? "could not read health snapshot");
    }

    /// <summary>
    /// Config-file viewing (the assistant's authorized-only <c>view_config_file</c>) is
    /// not wired on the Discord surface yet — degrade gracefully rather than break the
    /// shared port. Wire a MediatR query when Discord config-view is wanted.
    /// </summary>
    public Task<Result<string>> ReadInstanceFileAsync(
        string instance, string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<string>(
            "Viewing configuration files isn't available on the Discord surface yet."));

    private static Result Map(OperationResult result) =>
        result.IsSuccess ? Result.Success() : Result.Failure(result.ErrorMessage ?? "unknown error");
}
