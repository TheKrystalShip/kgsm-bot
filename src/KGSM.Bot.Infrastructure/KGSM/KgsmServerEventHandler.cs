
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
    private readonly IServerLabels _labels;
    private readonly ILogger<KgsmServerEventHandler> _logger;
    private readonly List<Func<ServerAnnouncement, Task>> _announcementHandlers = new();
    private readonly List<Func<string, string, Task>> _instanceInstalledHandlers = new();
    private readonly List<Func<string, Task>> _instanceUninstalledHandlers = new();
    private readonly List<Func<string, string, Task>> _instanceRenamedHandlers = new();

    public KgsmServerEventHandler(
        IKgsmClient kgsmClient,
        IServerLabels labels,
        ILogger<KgsmServerEventHandler> logger)
    {
        _kgsmClient = kgsmClient;
        _labels = labels;
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

        // The two halves of an update, and they are different facts: one says a newer build exists,
        // the other says this server is now running it. Both name the version pair they moved
        // between, so both render through DescribeVersions.
        //
        // The engine decides what is worth announcing here, not the bot: it records what each check
        // found and emits only for a version it has not announced before, so a channel sees one
        // message per new build however often the host checks.
        _kgsmClient.Events.RegisterHandler<InstanceUpdateAvailableData>(
            d => AnnounceAsync(AnnouncementKind.UpdateAvailable, d,
                DescribeVersions(d.CurrentVersion, d.LatestVersion)));

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

        // Not an announcement. A label somebody chose is not fleet news — nothing about the server
        // changed — but every surface holding the old one has to stop showing it, which is what the
        // renamed handlers do. The event carries both labels, so nothing has to go and ask.
        _kgsmClient.Events.RegisterHandler<InstanceDisplayNameChangedData>(OnInstanceRenamedAsync);

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

    /// <inheritdoc />
    public void RegisterInstanceRenamedHandler(Func<string, string, Task> handler)
    {
        _instanceRenamedHandlers.Add(handler);
    }

    /// <summary>
    /// Builds the announcement for an instance-scoped event and hands it to every registered
    /// handler. A handler that throws is logged and the rest still run — one broken reporter must
    /// not cost the others their event.
    /// </summary>
    private async Task AnnounceAsync(AnnouncementKind kind, EventDataBase data, string? detail = null)
    {
        // The event names the server by id, which is what it is keyed by everywhere and not what
        // anybody calls it. The label is read off the cached inventory here, once, so a message and
        // the status message beside it cannot disagree about what a server is called.
        string label = await _labels.LabelAsync(data.InstanceName);

        var announcement = new ServerAnnouncement(kind, data.InstanceName, detail, data.Actor, label);

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

        // The library is named beside the blueprint on a host with more than one disk, because which
        // one an install landed on is the fact that becomes hard to recover later.
        string? detail = (Trimmed(data.Blueprint), Trimmed(data.Library)) switch
        {
            (string blueprint, string library) => $"{blueprint} → {library}",
            (string blueprint, null) => blueprint,
            (null, string library) => library,
            _ => null,
        };

        await AnnounceAsync(AnnouncementKind.Installed, data, detail);
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
    /// Tells the bookkeeping side that a server is called something else now.
    /// </summary>
    /// <remarks>
    /// Deliberately silent. A rename changes nothing about the server — it is up or down exactly as
    /// it was a moment ago — so announcing it would spend a channel's attention on somebody fixing a
    /// typo. What it does change is every label already on screen, and those are refreshed by the
    /// handlers below rather than by a message.
    /// </remarks>
    private async Task OnInstanceRenamedAsync(InstanceDisplayNameChangedData data)
    {
        _logger.LogInformation("Instance {InstanceName} is now shown as {DisplayName} (was {OldDisplayName})",
            data.InstanceName, data.NewDisplayName, data.OldDisplayName);

        foreach (var handler in _instanceRenamedHandlers)
        {
            try
            {
                await handler(data.InstanceName, data.NewDisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling the rename of instance {InstanceName}", data.InstanceName);
            }
        }
    }

    /// <summary>
    /// Announces a crash once, on the first attempt of the burst.
    /// </summary>
    /// <remarks>
    /// The supervisor emits one <c>server.crashed</c> per restart attempt, so a server in a
    /// restart loop produces a run of them seconds apart — measured on this host, four crashes
    /// produced eleven events. Announcing each one turns a channel into a stack trace. The first
    /// attempt is the news; the outcome, if the supervisor runs out of attempts, arrives as
    /// <c>server.crash.exhausted</c> and is announced in its own right. A count that cannot be read is
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
