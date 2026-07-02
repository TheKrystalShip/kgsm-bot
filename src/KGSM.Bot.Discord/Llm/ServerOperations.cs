using KGSM.Bot.Application;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// Adapts the bot's <see cref="IServerService"/> to the assistant's
/// <see cref="IServerOperations"/> port. This keeps LLM-initiated actions on the
/// exact same path as the Discord slash commands, so behaviour is identical
/// regardless of which front end triggered the action.
/// </summary>
internal sealed class ServerOperations : IServerOperations
{
    private readonly IServerService _server;

    public ServerOperations(IServerService server) => _server = server;

    public async Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _server.StartAsync(instance, cancellationToken));

    public async Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _server.StopAsync(instance, cancellationToken));

    public async Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _server.RestartAsync(instance, cancellationToken));

    public async Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _server.CreateBackupAsync(instance, cancellationToken));

    public async Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _server.UpdateAsync(instance, cancellationToken));

    public async Task<Result> InstallAsync(string blueprint, string? instanceName, CancellationToken cancellationToken = default) =>
        Map(await _server.InstallAsync(blueprint, null, null, instanceName, cancellationToken));

    public async Task<Result> UninstallAsync(string instance, CancellationToken cancellationToken = default) =>
        Map(await _server.UninstallAsync(instance, cancellationToken));

    public async Task<Result> SetInstanceConfigValueAsync(
        string instance, string key, string value, CancellationToken cancellationToken = default) =>
        Map(await _server.SetConfigAsync(instance, key, value, cancellationToken));

    public async Task<Result<string>> GetStatusAsync(string instance, CancellationToken cancellationToken = default)
    {
        var result = await _server.GetStatusAsync(instance, cancellationToken);
        return result.IsSuccess
            ? Result.Success(result.Status ?? string.Empty)
            : Result.Failure<string>(result.ErrorMessage ?? "unknown error");
    }

    public async Task<Result<bool>> IsActiveAsync(string instance, CancellationToken cancellationToken = default)
    {
        var result = await _server.IsActiveAsync(instance, cancellationToken);
        return result.IsSuccess
            ? Result.Success(result.IsActive)
            : Result.Failure<bool>(result.ErrorMessage ?? "unknown error");
    }

    /// <summary>
    /// Fleet status for the shared <c>get_status</c> (no instance) tool. The fan-out
    /// over instances happens here in C# — it is ONE model-facing tool call, so it
    /// doesn't hit the agent-loop iteration cap the way a per-instance tool loop did.
    /// A per-instance liveness failure becomes an <see cref="FleetStatusAvailability.Unavailable"/>
    /// entry, never a fabricated "stopped".
    /// </summary>
    public async Task<Result<IReadOnlyList<FleetStatusEntry>>> GetFleetStatusAsync(CancellationToken cancellationToken = default)
    {
        var all = await _server.GetAllInstancesAsync(cancellationToken);
        if (!all.IsSuccess || all.Instances is null)
            return Result.Failure<IReadOnlyList<FleetStatusEntry>>(all.ErrorMessage ?? "could not list instances");

        var entries = new List<FleetStatusEntry>(all.Instances.Count);
        foreach (var name in all.Instances.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var active = await _server.IsActiveAsync(name, cancellationToken);
            entries.Add(active.IsSuccess
                ? new FleetStatusEntry(name, FleetStatusAvailability.Read, active.IsActive, Reason: null)
                : new FleetStatusEntry(name, FleetStatusAvailability.Unavailable, Running: null,
                    active.ErrorMessage ?? "status could not be read"));
        }

        return Result.Success<IReadOnlyList<FleetStatusEntry>>(entries);
    }

    public async Task<Result<InstanceHealthSnapshot>> GetHealthSnapshotAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        var result = await _server.GetHealthSnapshotAsync(instance, cancellationToken);
        return result.IsSuccess && result.Snapshot is not null
            ? Result.Success(result.Snapshot)
            : Result.Failure<InstanceHealthSnapshot>(result.ErrorMessage ?? "could not read health snapshot");
    }

    public Task<Result<string>> ReadInstanceFileAsync(
        string instance, string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<string>(
            "Viewing configuration files isn't available on the Discord surface yet."));

    public Task<Result<IReadOnlyList<InstanceDirEntry>>> ListInstanceDirectoryAsync(
        string instance, string? relativeSubdir = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<InstanceDirEntry>>(
            "Browsing server files isn't available on the Discord surface yet."));

    private static Result Map(OperationResult result) =>
        result.IsSuccess ? Result.Success() : Result.Failure(result.ErrorMessage ?? "unknown error");
}
