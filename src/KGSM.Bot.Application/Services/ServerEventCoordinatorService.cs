using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;

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
    private readonly IStatusBoard _statusBoard;
    private readonly IBackupInsight _backups;
    private readonly ILogger<ServerEventCoordinatorService> _logger;

    public ServerEventCoordinatorService(
        IServerEventHandler eventHandler,
        IDiscordNotificationService notificationService,
        IDiscordChannelRegistry channelRegistry,
        IKgsmStateCache stateCache,
        IStatusBoard statusBoard,
        IBackupInsight backups,
        ILogger<ServerEventCoordinatorService> logger)
    {
        _eventHandler = eventHandler;
        _notificationService = notificationService;
        _channelRegistry = channelRegistry;
        _stateCache = stateCache;
        _statusBoard = statusBoard;
        _backups = backups;
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
        {
            // Every event moves the picture, including the ones no guild announces: the live status
            // message is not a log of what was announced, it is what is true now. Marking it dirty is
            // a flag, so a burst costs one edit rather than one each — the board owns that decision.
            _statusBoard.Invalidate();

            // The set of backups a server has changes when the engine says it did, and at no other
            // time — so the cached summary is dropped on the event rather than expired on a timer.
            if (announcement.Kind is AnnouncementKind.BackupCreated or AnnouncementKind.BackupRestored)
                _backups.Invalidate(announcement.InstanceName);

            return _notificationService.AnnounceAsync(announcement);
        });

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

        _eventHandler.RegisterInstanceRenamedHandler(async (instanceName, displayName) =>
        {
            _logger.LogInformation("Server instance {InstanceName} is now shown as {DisplayName}",
                instanceName, displayName);

            // The label lives on the cached instance, so every surface reading that cache — the
            // board, the announcements, the autocomplete — keeps showing the old one until it is
            // dropped. Nothing else about the inventory moved.
            _stateCache.InvalidateInstances();
            _statusBoard.Invalidate();

            // The channel keeps its id-based name (Discord rate-limits a rename hard enough that a
            // bot cannot keep one in step with anything), so the topic is where the label goes.
            var result = await _channelRegistry.RefreshChannelTopicAsync(instanceName);
            if (result.IsFailure)
            {
                _logger.LogWarning("Could not refresh the channel topic for instance {InstanceName}: {Error}",
                    instanceName, result.Error);
            }
        });

        // Initialize the event handler
        _eventHandler.Initialize();

        _logger.LogInformation("Server event coordinator initialized");
    }
}
