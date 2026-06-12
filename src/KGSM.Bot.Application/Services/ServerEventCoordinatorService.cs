using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace KGSM.Bot.Application.Services;

/// <summary>
/// Service that handles server events and coordinates notifications
/// </summary>
public class ServerEventCoordinatorService
{
    private readonly IServerEventHandler _eventHandler;
    private readonly IDiscordNotificationService _notificationService;
    private readonly IDiscordChannelRegistry _channelRegistry;
    private readonly IKgsmStateCache _stateCache;
    private readonly ILogger<ServerEventCoordinatorService> _logger;

    public ServerEventCoordinatorService(
        IServerEventHandler eventHandler,
        IDiscordNotificationService notificationService,
        IDiscordChannelRegistry channelRegistry,
        IKgsmStateCache stateCache,
        ILogger<ServerEventCoordinatorService> logger)
    {
        _eventHandler = eventHandler;
        _notificationService = notificationService;
        _channelRegistry = channelRegistry;
        _stateCache = stateCache;
        _logger = logger;
    }

    public void Initialize(ulong guildId)
    {
        _logger.LogInformation("Initializing server event coordinator for guild {GuildId}", guildId);

        // Register event handlers
        _eventHandler.RegisterInstanceInstalledHandler(async (blueprintName, instanceName) =>
        {
            _logger.LogInformation("Server instance {InstanceName} installed using blueprint {BlueprintName}",
                instanceName, blueprintName);

            // Inventory changed — drop the cached instance list.
            _stateCache.InvalidateInstances();

            var result = await _channelRegistry.AddOrUpdateChannelAsync(guildId, blueprintName, instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to add channel for instance {InstanceName}: {Error}",
                    instanceName, result.Error);
            }
        });

        _eventHandler.RegisterInstanceStartedHandler(async (instanceName) =>
        {
            _logger.LogInformation("Server instance {InstanceName} started", instanceName);

            await _notificationService.NotifyRunningStatusUpdatedAsync(instanceName, InstanceStatus.Active);
        });

        _eventHandler.RegisterInstanceStoppedHandler(async (instanceName) =>
        {
            _logger.LogInformation("Server instance {InstanceName} stopped", instanceName);

            await _notificationService.NotifyRunningStatusUpdatedAsync(instanceName, InstanceStatus.Inactive);
        });

        _eventHandler.RegisterInstanceUninstalledHandler(async (instanceName) =>
        {
            _logger.LogInformation("Server instance {InstanceName} uninstalled", instanceName);

            // Inventory changed — drop the cached instance list.
            _stateCache.InvalidateInstances();

            // Uninstalled is essentially offline
            await _notificationService.NotifyRunningStatusUpdatedAsync(instanceName, InstanceStatus.Inactive);

            var result = await _channelRegistry.RemoveChannelAsync(guildId, instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to remove channel for instance {InstanceName}: {Error}",
                    instanceName, result.Error);
            }
        });

        // Initialize the event handler
        _eventHandler.Initialize();

        _logger.LogInformation("Server event coordinator initialized");
    }
}
