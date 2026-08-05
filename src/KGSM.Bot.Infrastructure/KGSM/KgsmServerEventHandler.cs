
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Implementation of IServerEventHandler using KGSM-Lib
/// </summary>
/// <remarks>
/// This is the one place kgsm-lib's event classes are known. Each subscription reduces its payload
/// to a <see cref="ServerAnnouncement"/> here, while the payload's type still says what its fields
/// mean, so nothing downstream has to switch over an engine type to render a message.
/// </remarks>
public class KgsmServerEventHandler : IServerEventHandler
{
    private readonly IKgsmClient _kgsmClient;
    private readonly ILogger<KgsmServerEventHandler> _logger;
    private readonly List<Func<ServerAnnouncement, Task>> _announcementHandlers = new();
    private readonly List<Func<string, string, Task>> _instanceInstalledHandlers = new();
    private readonly List<Func<string, Task>> _instanceUninstalledHandlers = new();

    public KgsmServerEventHandler(
        IKgsmClient kgsmClient,
        ILogger<KgsmServerEventHandler> logger)
    {
        _kgsmClient = kgsmClient;
        _logger = logger;
    }

    // Second guard, independent of the coordinator's: Initialize() starts the journal reader, and
    // a second call would run a second read loop over one reader's cursor state. Public entry
    // point, so it defends itself rather than trusting every caller.
    private bool _initialized;

    /// <inheritdoc />
    public void Initialize()
    {
        if (_initialized)
        {
            _logger.LogDebug("KGSM event handlers already initialized — skipping");
            return;
        }
        _initialized = true;

        _logger.LogInformation("Initializing KGSM event handlers");

        // Every subscription is a typed one. A raw handler would receive every envelope the journal
        // carries, including types this bot has no model for, and would have to be written to
        // tolerate a payload it did not expect; registering none leaves unknown-event tolerance
        // where kgsm-lib already implements it.
        _kgsmClient.Events.RegisterHandler<InstanceInstalledData>(OnInstanceInstalledAsync);
        _kgsmClient.Events.RegisterHandler<InstanceUninstalledData>(OnInstanceUninstalledAsync);

        _kgsmClient.Events.RegisterHandler<InstanceStartedData>(
            d => AnnounceAsync(AnnouncementKind.Started, d));
        _kgsmClient.Events.RegisterHandler<InstanceReadyData>(
            d => AnnounceAsync(AnnouncementKind.Ready, d));
        _kgsmClient.Events.RegisterHandler<InstanceStoppedData>(
            d => AnnounceAsync(AnnouncementKind.Stopped, d));
        _kgsmClient.Events.RegisterHandler<InstanceRestartedData>(
            d => AnnounceAsync(AnnouncementKind.Restarted, d));

        _kgsmClient.Events.RegisterHandler<InstanceCrashedData>(OnInstanceCrashedAsync);
        _kgsmClient.Events.RegisterHandler<InstanceFailedData>(
            d => AnnounceAsync(AnnouncementKind.Failed, d, DescribeExit(d.ExitCode, d.Restarts, "after")));

        // The version-carrying update event is the only one the engine emits: it reports an update
        // by naming the versions it moved between. There is no separate bare "updated" to subscribe
        // to, and subscribing to one would be a handler that never runs.
        _kgsmClient.Events.RegisterHandler<InstanceVersionUpdatedData>(
            d => AnnounceAsync(AnnouncementKind.Updated, d, DescribeVersions(d.OldVersion, d.NewVersion)));

        _kgsmClient.Events.RegisterHandler<InstanceBackupCreatedData>(
            d => AnnounceAsync(AnnouncementKind.BackupCreated, d, Trimmed(d.Version)));
        _kgsmClient.Events.RegisterHandler<InstanceBackupRestoredData>(
            d => AnnounceAsync(AnnouncementKind.BackupRestored, d, Trimmed(d.Version)));

        _kgsmClient.Events.RegisterHandler<InstancePlayerJoinedData>(
            d => AnnounceAsync(AnnouncementKind.PlayerJoined, d, DescribePlayer(d.PlayerName, d.PlayerId)));
        _kgsmClient.Events.RegisterHandler<InstancePlayerLeftData>(
            d => AnnounceAsync(AnnouncementKind.PlayerLeft, d, DescribePlayer(d.PlayerName, d.PlayerId)));

        _kgsmClient.Events.RegisterHandler<InstancePlayerKickedData>(
            d => AnnounceAsync(AnnouncementKind.PlayerKicked, d, Trimmed(d.Target)));
        _kgsmClient.Events.RegisterHandler<InstancePlayerBannedData>(
            d => AnnounceAsync(AnnouncementKind.PlayerBanned, d, Trimmed(d.Target)));
        _kgsmClient.Events.RegisterHandler<InstancePlayerUnbannedData>(
            d => AnnounceAsync(AnnouncementKind.PlayerUnbanned, d, Trimmed(d.Target)));

        // Initialize KGSM event listening
        _kgsmClient.Events.Initialize();

        _logger.LogInformation("KGSM event handlers initialized");
    }

    /// <inheritdoc />
    public void RegisterAnnouncementHandler(Func<ServerAnnouncement, Task> handler)
    {
        _announcementHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterInstanceInstalledHandler(Func<string, string, Task> handler)
    {
        _instanceInstalledHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterInstanceUninstalledHandler(Func<string, Task> handler)
    {
        _instanceUninstalledHandlers.Add(handler);
    }

    /// <summary>
    /// Builds the announcement for an instance-scoped event and hands it to every registered
    /// handler. A handler that throws is logged and the rest still run — one broken reporter must
    /// not cost the others their event.
    /// </summary>
    private async Task AnnounceAsync(AnnouncementKind kind, EventDataBase data, string? detail = null)
    {
        var announcement = new ServerAnnouncement(kind, data.InstanceName, detail, data.Actor);

        foreach (var handler in _announcementHandlers)
        {
            try
            {
                await handler(announcement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error announcing {Kind} for {InstanceName}",
                    kind, data.InstanceName);
            }
        }
    }

    private async Task OnInstanceInstalledAsync(InstanceInstalledData data)
    {
        _logger.LogInformation("Instance installed event: {InstanceName} using blueprint {BlueprintName}",
            data.InstanceName, data.Blueprint);

        // Before the announcement, not after: this is what creates the server's channel, and an
        // announcement sent first would have nowhere of its own to land.
        foreach (var handler in _instanceInstalledHandlers)
        {
            try
            {
                await handler(data.Blueprint, data.InstanceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling instance installed event for {InstanceName}",
                    data.InstanceName);
            }
        }

        await AnnounceAsync(AnnouncementKind.Installed, data, Trimmed(data.Blueprint));
    }

    private async Task OnInstanceUninstalledAsync(InstanceUninstalledData data)
    {
        _logger.LogInformation("Instance uninstalled event: {InstanceName}", data.InstanceName);

        // The mirror of install: announce first, because the handlers below may delete the very
        // channel the announcement is addressed to.
        await AnnounceAsync(AnnouncementKind.Uninstalled, data);

        foreach (var handler in _instanceUninstalledHandlers)
        {
            try
            {
                await handler(data.InstanceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling instance uninstalled event for {InstanceName}",
                    data.InstanceName);
            }
        }
    }

    /// <summary>
    /// Announces a crash once, on the first attempt of the burst.
    /// </summary>
    /// <remarks>
    /// The supervisor emits one <c>instance_crashed</c> per restart attempt, so a server in a
    /// restart loop produces a run of them seconds apart — measured on this host, four crashes
    /// produced eleven events. Announcing each one turns a channel into a stack trace. The first
    /// attempt is the news; the outcome, if the supervisor runs out of attempts, arrives as
    /// <c>instance_failed</c> and is announced in its own right. A count that cannot be read is
    /// announced rather than dropped — silence about a crash is the worse failure.
    /// </remarks>
    private Task OnInstanceCrashedAsync(InstanceCrashedData data)
    {
        bool firstAttempt = !int.TryParse(data.Restarts, out int restarts) || restarts <= 1;
        if (!firstAttempt)
        {
            _logger.LogDebug(
                "Instance crashed event: {InstanceName} (restart attempt {Restarts}) — already announced",
                data.InstanceName, data.Restarts);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Instance crashed event: {InstanceName} (exit {ExitCode})",
            data.InstanceName, data.ExitCode);

        return AnnounceAsync(AnnouncementKind.Crashed, data, DescribeExit(data.ExitCode, data.Restarts, "attempt"));
    }

    /// <summary>Renders an exit code and restart count, dropping whichever the event did not carry.</summary>
    private static string? DescribeExit(string exitCode, string restarts, string restartWord)
    {
        var parts = new List<string>(2);
        if (Trimmed(exitCode) is string code) parts.Add($"exit {code}");
        if (Trimmed(restarts) is string count) parts.Add($"{restartWord} {count}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Renders the version move, falling back to whichever end of it the event carried.</summary>
    private static string? DescribeVersions(string oldVersion, string newVersion) =>
        (Trimmed(oldVersion), Trimmed(newVersion)) switch
        {
            (string from, string to) => $"{from} → {to}",
            (null, string to) => to,
            (string from, null) => $"from {from}",
            _ => null,
        };

    /// <summary>
    /// Names a player by whichever identity the source gave. Both are nullable by contract — a game
    /// that reports only an id gets the id — and neither is invented to fill the other's place.
    /// </summary>
    private static string? DescribePlayer(string? playerName, string? playerId) =>
        Trimmed(playerName) ?? Trimmed(playerId);

    /// <summary>The value when it says something, null when it is blank — never an empty detail.</summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
