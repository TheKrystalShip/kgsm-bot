using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Implementation of IServerInstanceService using KGSM-Lib
/// </summary>
public class KgsmServerInstanceService : IServerInstanceService
{
    private readonly IKgsmClient _kgsmClient;
    private readonly KgsmOptions _options;
    private readonly ILogger<KgsmServerInstanceService> _logger;

    public KgsmServerInstanceService(
        IKgsmClient kgsmClient,
        IOptions<KgsmOptions> options,
        ILogger<KgsmServerInstanceService> logger)
    {
        _kgsmClient = kgsmClient;
        _options = options.Value;
        _logger = logger;
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
            await Task.Run(() => _kgsmClient.Instances.Install(blueprintName, instancePath, version, name));

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
            await Task.Run(() => _kgsmClient.Instances.Uninstall(instanceName));

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
            var result = await Task.Run(() => _kgsmClient.Instances.Start(instanceName));
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
            var result = await Task.Run(() => _kgsmClient.Instances.Stop(instanceName));
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
            var result = await Task.Run(() => _kgsmClient.Instances.Restart(instanceName));
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
            var result = await Task.Run(() => _kgsmClient.Instances.Update(instanceName));

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
    public async Task<Result> CreateBackupAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Creating backup for server instance {InstanceName}", instanceName);

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var result = await Task.Run(() => _kgsmClient.Instances.CreateBackup(instanceName));
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
    public async Task<Result<ulong?>> GetChannelIdAsync(string instanceName)
    {
        try
        {
            _logger.LogInformation("Getting channel ID for instance {InstanceName}", instanceName);

            // The channel id comes purely from local bot config — no kgsm involvement.
            // We deliberately do NOT spawn a full `GetAll()` enumeration just to existence-
            // check the instance: an unknown/unconfigured instance simply has no channel,
            // and the only caller (/list) already iterates instances it knows exist.
            ulong? channelId = null;

            if (_options.Instances.TryGetValue(instanceName, out var instanceConfig) &&
                ulong.TryParse(instanceConfig.ChannelId, out var parsedChannelId))
            {
                channelId = parsedChannelId;
            }

            _logger.LogInformation("Retrieved channel ID for instance {InstanceName}: {ChannelId}",
                instanceName, channelId?.ToString() ?? "null");
            return await Task.FromResult(Result.Success(channelId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting channel ID for instance {InstanceName}", instanceName);
            return Result.Failure<ulong?>(ex.Message);
        }
    }
}
