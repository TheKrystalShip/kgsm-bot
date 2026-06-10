using KGSM.Bot.Application.Queries;
using KGSM.Bot.Core.Interfaces;

using MediatR;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Application.Handlers;

/// <summary>
/// Query handler for getting all server instances
/// </summary>
public class GetAllInstancesQueryHandler : IRequestHandler<GetAllInstancesQuery, ServerInstancesResult>
{
    private readonly IKgsmStateCache _stateCache;
    private readonly ILogger<GetAllInstancesQueryHandler> _logger;

    public GetAllInstancesQueryHandler(
        IKgsmStateCache stateCache,
        ILogger<GetAllInstancesQueryHandler> logger)
    {
        _stateCache = stateCache;
        _logger = logger;
    }

    public async Task<ServerInstancesResult> Handle(GetAllInstancesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Getting all server instances");

            // Inventory read — served from the cache (TTL + event invalidation) so the
            // hot path (autocomplete fires per keystroke, /list) doesn't spawn a kgsm
            // subprocess every time. The cache never fails hard; it serves a stale or
            // empty snapshot and logs internally.
            var instances = await _stateCache.GetInstancesAsync(cancellationToken);

            _logger.LogInformation("Successfully retrieved {Count} server instances", instances.Count);
            return ServerInstancesResult.Success(instances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all server instances");
            return ServerInstancesResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Query handler for getting all blueprints
/// </summary>
public class GetAllBlueprintsQueryHandler : IRequestHandler<GetAllBlueprintsQuery, BlueprintsResult>
{
    private readonly IKgsmStateCache _stateCache;
    private readonly ILogger<GetAllBlueprintsQueryHandler> _logger;

    public GetAllBlueprintsQueryHandler(
        IKgsmStateCache stateCache,
        ILogger<GetAllBlueprintsQueryHandler> logger)
    {
        _stateCache = stateCache;
        _logger = logger;
    }

    public async Task<BlueprintsResult> Handle(GetAllBlueprintsQuery request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Getting all blueprints");

            // Inventory read — served from the cache (see GetAllInstancesQueryHandler).
            // Blueprints rarely change, so this is almost always a memory hit.
            var blueprints = await _stateCache.GetBlueprintsAsync(cancellationToken);

            _logger.LogInformation("Successfully retrieved {Count} blueprints", blueprints.Count);
            return BlueprintsResult.Success(blueprints);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all blueprints");
            return BlueprintsResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Query handler for checking server status
/// </summary>
public class GetServerStatusQueryHandler : IRequestHandler<GetServerStatusQuery, ServerStatusResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<GetServerStatusQueryHandler> _logger;

    public GetServerStatusQueryHandler(
        IServerInstanceService serverInstanceService,
        ILogger<GetServerStatusQueryHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<ServerStatusResult> Handle(GetServerStatusQuery request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Getting status for server instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.GetInfoAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to get status for server instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return ServerStatusResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully retrieved status for server instance {InstanceName}",
                request.InstanceName);
            return ServerStatusResult.Success(result.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for server instance {InstanceName}",
                request.InstanceName);
            return ServerStatusResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Query handler for checking if a server is active
/// </summary>
public class IsServerActiveQueryHandler : IRequestHandler<IsServerActiveQuery, ServerActiveResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<IsServerActiveQueryHandler> _logger;

    public IsServerActiveQueryHandler(
        IServerInstanceService serverInstanceService,
        ILogger<IsServerActiveQueryHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<ServerActiveResult> Handle(IsServerActiveQuery request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Checking if server instance {InstanceName} is active", request.InstanceName);

            var result = await _serverInstanceService.IsActiveAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to check if server instance {InstanceName} is active: {Error}",
                    request.InstanceName, result.Error);
                return ServerActiveResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully checked if server instance {InstanceName} is active: {IsActive}",
                request.InstanceName, result.Value);
            return ServerActiveResult.Success(result.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if server instance {InstanceName} is active",
                request.InstanceName);
            return ServerActiveResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}

/// <summary>
/// Query handler for getting an instance's Discord channel ID
/// </summary>
public class GetInstanceChannelIdQueryHandler : IRequestHandler<GetInstanceChannelIdQuery, InstanceChannelIdResult>
{
    private readonly IServerInstanceService _serverInstanceService;
    private readonly ILogger<GetInstanceChannelIdQueryHandler> _logger;

    public GetInstanceChannelIdQueryHandler(
        IServerInstanceService serverInstanceService,
        ILogger<GetInstanceChannelIdQueryHandler> logger)
    {
        _serverInstanceService = serverInstanceService;
        _logger = logger;
    }

    public async Task<InstanceChannelIdResult> Handle(GetInstanceChannelIdQuery request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Getting channel ID for instance {InstanceName}", request.InstanceName);

            var result = await _serverInstanceService.GetChannelIdAsync(request.InstanceName);

            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to get channel ID for instance {InstanceName}: {Error}",
                    request.InstanceName, result.Error);
                return InstanceChannelIdResult.Failure(result.Error ?? "Unknown error");
            }

            _logger.LogInformation("Successfully retrieved channel ID for instance {InstanceName}: {ChannelId}",
                request.InstanceName, result.Value?.ToString() ?? "null");
            return InstanceChannelIdResult.Success(result.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting channel ID for instance {InstanceName}", request.InstanceName);
            return InstanceChannelIdResult.Failure($"An error occurred: {ex.Message}");
        }
    }
}
