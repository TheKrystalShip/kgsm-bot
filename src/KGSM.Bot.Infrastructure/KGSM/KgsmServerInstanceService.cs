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
    public async Task<Result> InstallAsync(string blueprintName, string? instancePath = null, string? version = null, string? name = null)
    {
        try
        {
            _logger.LogInformation("Installing server instance from blueprint {BlueprintName} at path {Path} with version {Version} and name {Name}",
                blueprintName, instancePath, version, name);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var (actor, origin) = Provenance();
            await Task.Run(() => _kgsmClient.Instances.Install(blueprintName, instancePath, version, name, actor, origin));

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
