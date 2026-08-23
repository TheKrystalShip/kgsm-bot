using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

// KGSM.Lib 1.1.0 added TheKrystalShip.KGSM.Core.Models.KgsmOptions, which collides with
// the bot's own config type; pin the unqualified name to the bot's.
using KgsmOptions = KGSM.Bot.Infrastructure.Configuration.KgsmOptions;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Implementation of IServerInstanceService using KGSM-Lib
/// </summary>
public class KgsmServerInstanceService : IServerInstanceService
{
    private readonly IKgsmClient _kgsmClient;
    private readonly KgsmOptions _options;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<KgsmServerInstanceService> _logger;

    public KgsmServerInstanceService(
        IKgsmClient kgsmClient,
        IOptions<KgsmOptions> options,
        IInvocationContext invocation,
        ILogger<KgsmServerInstanceService> logger)
    {
        _kgsmClient = kgsmClient;
        _options = options.Value;
        _invocation = invocation;
        _logger = logger;
    }

    // The provenance of the action being performed (set at the entry point), or (null, null) for a
    // non-attributed/background path — KGSM then applies its honest fallback, never a fabricated actor.
    private (string? Actor, string? Origin) Provenance()
    {
        Invocation? inv = _invocation.Current;
        return (inv?.Actor, inv?.Origin);
    }    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, Instance>>> GetAllAsync()
    {
        try
        {
            _logger.LogInformation("Getting all server instances");

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var instances = await Task.Run(() => _kgsmClient.Instances.GetAll());

            _logger.LogInformation("Retrieved {Count} server instances", instances.Count);
            return Result.Success<IReadOnlyDictionary<string, Instance>>(instances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting server instances");
            return Result.Failure<IReadOnlyDictionary<string, Instance>>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> InstallAsync(string blueprintName, string? library = null, string? version = null, string? name = null)
    {
        try
        {
            _logger.LogInformation("Installing server instance from blueprint {BlueprintName} into library {Library} with version {Version} and name {Name}",
                blueprintName, library, version, name);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            await Task.Run(() => _kgsmClient.Instances.Install(blueprintName, library, version, name, actor, origin));

            _logger.LogInformation("Successfully installed server instance from blueprint {BlueprintName}",
                blueprintName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing server instance from blueprint {BlueprintName}",
                blueprintName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Library>>> GetLibrariesAsync()
    {
        try
        {
            // Never cached: free space is a measurement, and a library goes offline the moment its
            // disk is unmounted. A stale answer here would offer somewhere to install that is gone.
            var libraries = await Task.Run(() => _kgsmClient.Libraries.List());

            // Null is a failed read, not a host with nothing registered — the engine emits an empty
            // array for that. Reporting it as success would offer "no libraries" as a fact.
            if (libraries is null)
            {
                _logger.LogWarning("Could not read the library list from KGSM");
                return Result.Failure<IReadOnlyList<Library>>("Could not read the library list from KGSM");
            }

            _logger.LogInformation("Retrieved {Count} libraries", libraries.Count);
            return Result.Success<IReadOnlyList<Library>>(libraries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting libraries");
            return Result.Failure<IReadOnlyList<Library>>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> UninstallAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Uninstalling server instance {InstanceName}", instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            await Task.Run(() => _kgsmClient.Instances.Uninstall(instanceName, actor, origin));

            _logger.LogInformation("Successfully uninstalled server instance {InstanceName}",
                instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uninstalling server instance {InstanceName}",
                instanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> StartAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Starting server instance {InstanceName}", instanceName);

            // Check if the instance is already running
            bool isActive = await Task.Run(() => _kgsmClient.Instances.IsActive(instanceName));
            if (isActive)
            {
                _logger.LogInformation("Server instance {InstanceName} is already running", instanceName);
                return Result.Success();
            }

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            var result = await Task.Run(() => _kgsmClient.Instances.Start(instanceName, actor, origin));
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to start server instance {InstanceName}: {Error}",
                    instanceName, result.Stderr);
                return Result.Failure(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Successfully started server instance {InstanceName}", instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server instance {InstanceName}", instanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> StopAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Stopping server instance {InstanceName}", instanceName);

            // Check if the instance is already stopped
            bool isActive = await Task.Run(() => _kgsmClient.Instances.IsActive(instanceName));
            if (!isActive)
            {
                _logger.LogInformation("Server instance {InstanceName} is already stopped", instanceName);
                return Result.Success();
            }

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            var result = await Task.Run(() => _kgsmClient.Instances.Stop(instanceName, actor, origin));
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to stop server instance {InstanceName}: {Error}",
                    instanceName, result.Stderr);
                return Result.Failure(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Successfully stopped server instance {InstanceName}", instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server instance {InstanceName}", instanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> RestartAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Restarting server instance {InstanceName}", instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            var result = await Task.Run(() => _kgsmClient.Instances.Restart(instanceName, actor, origin));
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to restart server instance {InstanceName}: {Error}",
                    instanceName, result.Stderr);
                return Result.Failure(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Successfully restarted server instance {InstanceName}", instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting server instance {InstanceName}", instanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Updating server instance {InstanceName}", instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            var result = await Task.Run(() => _kgsmClient.Instances.Update(instanceName, actor, origin));

            if (result.IsFailure)
            {
                _logger.LogWarning("Error updating server instance {InstanceName}: {Error}",
                    instanceName, result.Stderr);
                return Result.Failure(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Successfully updated server instance {InstanceName}", instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating server instance {InstanceName}", instanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetInfoAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Getting info for server instance {InstanceName}", instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var result = await Task.Run(() => _kgsmClient.Instances.GetInfo(instanceName));

            if (result.IsFailure)
            {
                _logger.LogWarning("Error getting info for server instance {InstanceName}: {Error}",
                    instanceName, result.Stderr);
                return Result.Failure<string>(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Successfully got info for server instance {InstanceName}", instanceName);
            return Result.Success(result.Stdout ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting info for server instance {InstanceName}", instanceName);
            return Result.Failure<string>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> GetLogsAsync(string instanceName, int lines)
    {
        try
        {
            _logger.LogInformation("Reading the last {Lines} log lines for server instance {InstanceName}",
                lines, instanceName);

            ICollection<string> log = await _kgsmClient.Instances.GetLogsAsync(instanceName, lines);

            return Result.Success<IReadOnlyList<string>>([.. log]);
        }
        catch (Exception ex)
        {
            // kgsm-lib throws rather than returning a failure here, so an unreadable log arrives as an
            // exception and becomes a failed Result like every other answer this service gives.
            _logger.LogError(ex, "Error reading logs for server instance {InstanceName}", instanceName);
            return Result.Failure<IReadOnlyList<string>>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsActiveAsync(string instanceName)
    {
        try
        {
            _logger.LogDebug("Checking if server instance {InstanceName} is active", instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            bool isActive = await Task.Run(() => _kgsmClient.Instances.IsActive(instanceName));

            _logger.LogDebug("Server instance {InstanceName} is {Status}",
                instanceName, isActive ? "active" : "inactive");
            return Result.Success(isActive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if server instance {InstanceName} is active", instanceName);
            return Result.Failure<bool>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<InstanceRuntimeStatus>> GetRuntimeStatusAsync(string instanceName)
    {
        try
        {
            _logger.LogDebug("Getting runtime status for server instance {InstanceName}", instanceName);

            // Non-fast: performs the per-instance update check, so the version block is real.
            var status = await Task.Run(() => _kgsmClient.Instances.GetInstanceStatus(instanceName));
            if (status is null)
                return Result.Failure<InstanceRuntimeStatus>(
                    $"'{instanceName}' did not return a status (it may need its management file regenerated).");

            return Result.Success(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting runtime status for server instance {InstanceName}", instanceName);
            return Result.Failure<InstanceRuntimeStatus>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<SystemInfo>> GetSystemInfoAsync()
    {
        try
        {
            var info = await Task.Run(() => _kgsmClient.System.GetSystemInfo());
            return info is null
                ? Result.Failure<SystemInfo>("host system info was unavailable")
                : Result.Success(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting host system info");
            return Result.Failure<SystemInfo>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> CreateBackupAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Creating backup for server instance {InstanceName}", instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            var result = await Task.Run(() => _kgsmClient.Instances.CreateBackup(instanceName, actor, origin));
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to create backup for server instance {InstanceName}: {Error}",
                    instanceName, result.Stderr);
                return Result.Failure(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Successfully created backup for server instance {InstanceName}", instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup for server instance {InstanceName}", instanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InstanceBackup>>> GetBackupsAsync(string instanceName)
    {
        try
        {
            _logger.LogDebug("Reading backups for server instance {InstanceName}", instanceName);

            List<InstanceBackup> backups = await Task.Run(() => _kgsmClient.Instances.GetBackupsDetailed(instanceName));

            // Newest first, and a backup whose manifest carries no timestamp sorts last rather than
            // being dropped — it exists, and a restore can still name it.
            return Result.Success<IReadOnlyList<InstanceBackup>>(
                [.. backups.OrderByDescending(b => b.CreatedAt ?? DateTimeOffset.MinValue)]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading backups for server instance {InstanceName}", instanceName);
            return Result.Failure<IReadOnlyList<InstanceBackup>>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> RestoreBackupAsync(string instanceName, string backupId)
    {
        try
        {
            _logger.LogInformation("Restoring backup {BackupId} onto server instance {InstanceName}",
                backupId, instanceName);

            var (actor, origin) = Provenance();
            var result = await Task.Run(() => _kgsmClient.Instances.RestoreBackup(instanceName, backupId, actor, origin));
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to restore backup {BackupId} onto {InstanceName}: {Error}",
                    backupId, instanceName, result.Stderr);
                return Result.Failure(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Restored backup {BackupId} onto server instance {InstanceName}",
                backupId, instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring backup {BackupId} onto server instance {InstanceName}",
                backupId, instanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> SetConfigValueAsync(string instanceName, string key, string value)
    {
        try
        {
            _logger.LogInformation("Setting config '{Key}' on server instance {InstanceName}", key, instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            var result = await Task.Run(() => _kgsmClient.Instances.SetInstanceConfigValue(instanceName, key, value, actor, origin));
            if (result.IsFailure)
            {
                // kgsm refuses denylisted/invalid keys with a clear stderr message — surface it.
                _logger.LogWarning("Failed to set config '{Key}' on server instance {InstanceName}: {Error}",
                    key, instanceName, result.Stderr);
                return Result.Failure(result.Stderr ?? "Unknown error");
            }

            _logger.LogInformation("Successfully set config '{Key}' on server instance {InstanceName}", key, instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting config '{Key}' on server instance {InstanceName}", key, instanceName);
            return Result.Failure(ex.Message);
        }
    }
}
