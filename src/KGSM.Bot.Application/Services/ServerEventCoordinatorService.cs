using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

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

    // Guards against re-registration. The caller is Discord's gateway READY handler, which fires
    // again on every reconnect — and each pass here APPENDS to the event handler's callback lists,
    // so an unguarded second call makes every lifecycle event announce twice, a third makes it
    // three times, and so on for the life of the process. It also re-entered
    // IServerEventHandler.Initialize, starting a second journal reader over the same cursor state.
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
        {
            _logger.LogDebug("Server event coordinator already initialized — skipping re-registration");
            return;
        }
        _initialized = true;

        _logger.LogInformation("Initializing server event coordinator");

        // The reporting side: one handler for every announceable event. Which of them reaches a
        // channel is the notification service's decision, made against the operator's toggles —
        // this wiring reports everything and suppresses nothing.
        _eventHandler.RegisterAnnouncementHandler(announcement =>
            _notificationService.AnnounceAsync(announcement));

        // The bookkeeping side: what has to happen whether or not anything is announced.
        _eventHandler.RegisterInstanceInstalledHandler(async (blueprintName, instanceName) =>
        {
            _logger.LogInformation("Server instance {InstanceName} installed using blueprint {BlueprintName}",
                instanceName, blueprintName);

            // Inventory changed — drop the cached instance list.
            _stateCache.InvalidateInstances();

            // Which guilds get a channel is the registry's business: it is whoever turned a board on,
            // and that is a fact about the store rather than about this event.
            var result = await _channelRegistry.AddOrUpdateChannelAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to add channel for instance {InstanceName}: {Error}",
                    instanceName, result.Error);
            }
        });

        _eventHandler.RegisterInstanceUninstalledHandler(async (instanceName) =>
        {
            _logger.LogInformation("Server instance {InstanceName} uninstalled", instanceName);

            // Inventory changed — drop the cached instance list.
            _stateCache.InvalidateInstances();

            var result = await _channelRegistry.RemoveChannelAsync(instanceName);
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
