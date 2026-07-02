using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace KGSM.Bot.Application;

/// <summary>
/// Consolidated server operations — the direct-call replacement for the MediatR
/// command/query dispatcher. Wraps try/catch + logging + result mapping in one place.
/// </summary>
public sealed class ServerService : IServerService
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly IKgsmStateCache _stateCache;
    private readonly IWatchdogService _watchdogService;
    private readonly ILogger<ServerService> _logger;

    public ServerService(
        IServerInstanceService serverInstanceService,
        IKgsmStateCache stateCache,
        IWatchdogService watchdogService,
        ILogger<ServerService> logger)
    {
        _serverInstanceService = serverInstanceService;
        _stateCache = stateCache;
        _watchdogService = watchdogService;
        _logger = logger;
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public async Task<OperationResult> StartAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting server instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.StartAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to start server instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully started server instance {InstanceName}", instanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server instance {InstanceName}", instanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<OperationResult> StopAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Stopping server instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.StopAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to stop server instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully stopped server instance {InstanceName}", instanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server instance {InstanceName}", instanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<OperationResult> RestartAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Restarting server instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.RestartAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to restart server instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully restarted server instance {InstanceName}", instanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting server instance {InstanceName}", instanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<OperationResult> InstallAsync(string blueprintName, string? path, string? version, string? name, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Installing server instance from blueprint {BlueprintName} at path {Path} with version {Version} and name {Name}",
                blueprintName, path ?? "default", version ?? "default", name ?? "auto-generated");
            var result = await _serverInstanceService.InstallAsync(blueprintName, path, version, name);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to install server instance from blueprint {BlueprintName}: {Error}",
                    blueprintName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully installed server instance from blueprint {BlueprintName}", blueprintName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing server instance from blueprint {BlueprintName}", blueprintName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<OperationResult> UninstallAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Uninstalling server instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.UninstallAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to uninstall server instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully uninstalled server instance {InstanceName}", instanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uninstalling server instance {InstanceName}", instanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<OperationResult> CreateBackupAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Creating backup for server instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.CreateBackupAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to create backup for server instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully created backup for server instance {InstanceName}", instanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup for server instance {InstanceName}", instanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<OperationResult> UpdateAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Updating server instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.UpdateAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to update server instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully updated server instance {InstanceName}", instanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating server instance {InstanceName}", instanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<OperationResult> SetConfigAsync(string instanceName, string key, string value, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Setting config '{Key}' on server instance {InstanceName}", key, instanceName);
            var result = await _serverInstanceService.SetConfigValueAsync(instanceName, key, value);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to set config '{Key}' on server instance {InstanceName}: {Error}",
                    key, instanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully set config '{Key}' on server instance {InstanceName}", key, instanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting config '{Key}' on server instance {InstanceName}", key, instanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    // ── Queries ───────────────────────────────────────────────────────────

    public async Task<ServerInstancesResult> GetAllInstancesAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Getting all server instances");
            var instances = await _stateCache.GetInstancesAsync(ct);
            _logger.LogInformation("Successfully retrieved {Count} server instances", instances.Count);
            return ServerInstancesResult.Success(instances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all server instances");
            return ServerInstancesResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<BlueprintsResult> GetAllBlueprintsAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Getting all blueprints");
            var blueprints = await _stateCache.GetBlueprintsAsync(ct);
            _logger.LogInformation("Successfully retrieved {Count} blueprints", blueprints.Count);
            return BlueprintsResult.Success(blueprints);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all blueprints");
            return BlueprintsResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<ServerStatusResult> GetStatusAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Getting status for server instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.GetInfoAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to get status for server instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return ServerStatusResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully retrieved status for server instance {InstanceName}", instanceName);
            return ServerStatusResult.Success(result.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for server instance {InstanceName}", instanceName);
            return ServerStatusResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<ServerActiveResult> IsActiveAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Checking if server instance {InstanceName} is active", instanceName);
            var result = await _serverInstanceService.IsActiveAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to check if server instance {InstanceName} is active: {Error}",
                    instanceName, result.Error);
                return ServerActiveResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully checked if server instance {InstanceName} is active: {IsActive}",
                instanceName, result.Value);
            return ServerActiveResult.Success(result.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if server instance {InstanceName} is active", instanceName);
            return ServerActiveResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<HealthSnapshotResult> GetHealthSnapshotAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Building health snapshot for instance {InstanceName}", instanceName);

            var status = await _serverInstanceService.GetRuntimeStatusAsync(instanceName);
            if (status.IsFailure || status.Value is null)
                return HealthSnapshotResult.Failure(status.Error ?? "could not read status");

            var s = status.Value;

            // Host disk is best-effort: its absence skips the disk check, never fails the read.
            HostDisk? hostDisk = null;
            string? diskReason = null;
            var disk = await _serverInstanceService.GetSystemInfoAsync();
            if (disk.IsSuccess && disk.Value is not null)
                hostDisk = new HostDisk(
                    ParsePercent(disk.Value.Disk.UsePercent), disk.Value.Disk.Size, disk.Value.Disk.Available);
            else
                diskReason = disk.Error ?? "the host disk usage could not be read";

            var snapshot = new InstanceHealthSnapshot(
                Running: s.Status,
                RecentLogLines: SplitLogLines(s.RecentLogs),
                UpdatesAvailable: s.Version.UpdatesAvailable,
                CurrentVersion: NullIfEmpty(s.Version.Current),
                LatestVersion: s.Version.Latest,
                HostDisk: hostDisk,
                HostDiskUnavailableReason: diskReason);

            return HealthSnapshotResult.Success(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building health snapshot for instance {InstanceName}", instanceName);
            return HealthSnapshotResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<InstanceChannelIdResult> GetChannelIdAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Getting channel ID for instance {InstanceName}", instanceName);
            var result = await _serverInstanceService.GetChannelIdAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to get channel ID for instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return InstanceChannelIdResult.Failure(result.Error ?? "Unknown error");
            }
            _logger.LogInformation("Successfully retrieved channel ID for instance {InstanceName}: {ChannelId}",
                instanceName, result.Value?.ToString() ?? "null");
            return InstanceChannelIdResult.Success(result.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting channel ID for instance {InstanceName}", instanceName);
            return InstanceChannelIdResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    public async Task<WatchdogStatusResult> GetWatchdogStatusAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Getting watchdog supervision state for instance {InstanceName}", instanceName);
            var result = await _watchdogService.GetStatusAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to get watchdog state for instance {InstanceName}: {Error}",
                    instanceName, result.Error);
                return WatchdogStatusResult.Failure(result.Error ?? "Unknown error");
            }
            return WatchdogStatusResult.Success(result.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting watchdog state for instance {InstanceName}", instanceName);
            return WatchdogStatusResult.Failure($"An error occurred: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IReadOnlyList<string> SplitLogLines(string? recentLogs) =>
        string.IsNullOrEmpty(recentLogs)
            ? Array.Empty<string>()
            : recentLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int? ParsePercent(string? usePercent)
    {
        if (string.IsNullOrWhiteSpace(usePercent))
            return null;
        var digits = new string(usePercent.TrimStart().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var pct) ? pct : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
