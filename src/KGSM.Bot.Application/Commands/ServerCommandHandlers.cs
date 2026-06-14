using KGSM.Bot.Application.Commands;
using KGSM.Bot.Core.Interfaces;

using MediatR;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Application.Handlers;

/// <summary>
/// Command handler for starting a server instance
/// </summary>
public class StartServerCommandHandler : IRequestHandler<StartServerCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<StartServerCommandHandler> _logger;

    public StartServerCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<StartServerCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(StartServerCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting server instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.StartAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to start server instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully started server instance {InstanceName}", request.InstanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server instance {InstanceName}", request.InstanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Command handler for stopping a server instance
/// </summary>
public class StopServerCommandHandler : IRequestHandler<StopServerCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<StopServerCommandHandler> _logger;

    public StopServerCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<StopServerCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(StopServerCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Stopping server instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.StopAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to stop server instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully stopped server instance {InstanceName}", request.InstanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server instance {InstanceName}", request.InstanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Command handler for installing a server instance
/// </summary>
public class InstallServerCommandHandler : IRequestHandler<InstallServerCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<InstallServerCommandHandler> _logger;

    public InstallServerCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<InstallServerCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(InstallServerCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Installing server instance from blueprint {BlueprintName} at path {Path} with version {Version} and name {Name}",
                request.BlueprintName,
                request.InstallPath ?? "default",
                request.Version ?? "default",
                request.Name ?? "auto-generated");

            var result = await _serverInstanceService.InstallAsync(
                request.BlueprintName,
                request.InstallPath,
                request.Version,
                request.Name);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to install server instance from blueprint {BlueprintName}: {Error}",
                    request.BlueprintName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully installed server instance from blueprint {BlueprintName}",
                request.BlueprintName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing server instance from blueprint {BlueprintName}",
                request.BlueprintName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Command handler for uninstalling a server instance
/// </summary>
public class UninstallServerCommandHandler : IRequestHandler<UninstallServerCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<UninstallServerCommandHandler> _logger;

    public UninstallServerCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<UninstallServerCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(UninstallServerCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Uninstalling server instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.UninstallAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to uninstall server instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully uninstalled server instance {InstanceName}",
                request.InstanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uninstalling server instance {InstanceName}", request.InstanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Command handler for creating a server instance backup
/// </summary>
public class CreateBackupCommandHandler : IRequestHandler<CreateBackupCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<CreateBackupCommandHandler> _logger;

    public CreateBackupCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<CreateBackupCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(CreateBackupCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Creating backup for server instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.CreateBackupAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to create backup for server instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully created backup for server instance {InstanceName}",
                request.InstanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup for server instance {InstanceName}", request.InstanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Command handler for restarting a server instance
/// </summary>
public class RestartServerCommandHandler : IRequestHandler<RestartServerCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<RestartServerCommandHandler> _logger;

    public RestartServerCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<RestartServerCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(RestartServerCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Restarting server instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.RestartAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to restart server instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully restarted server instance {InstanceName}",
                request.InstanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting server instance {InstanceName}", request.InstanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Command handler for updating a server instance
/// </summary>
public class UpdateServerCommandHandler : IRequestHandler<UpdateServerCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<UpdateServerCommandHandler> _logger;

    public UpdateServerCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<UpdateServerCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(UpdateServerCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Updating server instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.UpdateAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to update server instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully updated server instance {InstanceName}",
                request.InstanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating server instance {InstanceName}", request.InstanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Command handler for setting a single config key=value on a server instance
/// </summary>
public class SetInstanceConfigCommandHandler : IRequestHandler<SetInstanceConfigCommand, OperationResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<SetInstanceConfigCommandHandler> _logger;

    public SetInstanceConfigCommandHandler(
        IServerInstanceService serverInstanceService,
        ILogger<SetInstanceConfigCommandHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<OperationResult> Handle(SetInstanceConfigCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Setting config '{Key}' on server instance {InstanceName}",
                request.Key, request.InstanceName);

            var result = await _serverInstanceService.SetConfigValueAsync(request.InstanceName, request.Key, request.Value);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to set config '{Key}' on server instance {InstanceName}: {Error}",
                    request.Key, request.InstanceName, result.Error);
                return OperationResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully set config '{Key}' on server instance {InstanceName}",
                request.Key, request.InstanceName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting config '{Key}' on server instance {InstanceName}",
                request.Key, request.InstanceName);
            return OperationResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}
