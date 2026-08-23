using KGSM.Bot.Core.Common;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Interface for managing game server instances
/// </summary>
public interface IServerInstanceService
{
    /// <summary>
    /// Gets all server instances
    /// </summary>
    /// <returns>Collection of server instances</returns>
    Task<Result<IReadOnlyDictionary<string, Instance>>> GetAllAsync();

    /// <summary>
    /// Installs a new server instance from a blueprint
    /// </summary>
    /// <param name="blueprintName">Name of the blueprint to use</param>
    /// <param name="library">Library to place the instance in (optional; absent lets the engine resolve it)</param>
    /// <param name="version">Version to install (optional)</param>
    /// <param name="name">Name for the instance (optional)</param>
    /// <returns>Result of the operation</returns>
    Task<Result> InstallAsync(string blueprintName, string? library = null, string? version = null, string? name = null);

    /// <summary>
    /// Lists the libraries instances can be placed in.
    /// </summary>
    /// <returns>The registered libraries, or a failure when the engine could not be read</returns>
    Task<Result<IReadOnlyList<Library>>> GetLibrariesAsync();

    /// <summary>
    /// Uninstalls a server instance
    /// </summary>
    /// <param name="instanceName">Name of the instance to uninstall</param>
    /// <returns>Result of the operation</returns>
    Task<Result> UninstallAsync(string instanceName);

    /// <summary>
    /// Starts a server instance
    /// </summary>
    /// <param name="instanceName">Name of the instance to start</param>
    /// <returns>Result of the operation</returns>
    Task<Result> StartAsync(string instanceName);

    /// <summary>
    /// Stops a server instance
    /// </summary>
    /// <param name="instanceName">Name of the instance to stop</param>
    /// <returns>Result of the operation</returns>
    Task<Result> StopAsync(string instanceName);

    /// <summary>
    /// Restarts a server instance
    /// </summary>
    /// <param name="instanceName">Name of the instance to restart</param>
    /// <returns>Result of the operation</returns>
    Task<Result> RestartAsync(string instanceName);

    /// <summary>
    /// Updates a server instance to the latest available version
    /// </summary>
    /// <param name="instanceName">Name of the instance to update</param>
    /// <returns>Result of the operation</returns>
    Task<Result> UpdateAsync(string instanceName);

    /// <summary>
    /// Gets information about a server instance
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>Information result</returns>
    Task<Result<string>> GetInfoAsync(string instanceName);

    /// <summary>
    /// Gets the tail of an instance's log, newest last.
    /// </summary>
    /// <remarks>
    /// The engine owns where a server's output lands and how far back it goes; this asks for the last
    /// <paramref name="lines"/> of it and nothing more. An empty list is a real answer — a server that
    /// has written nothing yet — and is not a failure.
    /// </remarks>
    /// <param name="instanceName">Name of the instance</param>
    /// <param name="lines">How many lines from the end to read</param>
    /// <returns>The lines, or a failure if the log could not be read.</returns>
    Task<Result<IReadOnlyList<string>>> GetLogsAsync(string instanceName, int lines);

    /// <summary>
    /// Checks if the instance is currently active (running)
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>True if active, false otherwise</returns>
    Task<Result<bool>> IsActiveAsync(string instanceName);

    /// <summary>
    /// Gets the structured runtime status (running-state, recent logs, version) for an
    /// instance — the typed object behind <see cref="GetInfoAsync"/>'s text. Backs the
    /// assistant's <c>run_health_check</c> aggregator.
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>The runtime status, or a failure if it could not be read.</returns>
    Task<Result<InstanceRuntimeStatus>> GetRuntimeStatusAsync(string instanceName);

    /// <summary>
    /// Gets host system information (disk / memory / load) for the health check's disk
    /// headroom check. Not instance-specific.
    /// </summary>
    /// <returns>The host system info, or a failure if it could not be read.</returns>
    Task<Result<SystemInfo>> GetSystemInfoAsync();

    /// <summary>
    /// Creates a backup of the instance
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>Result of the operation</returns>
    Task<Result> CreateBackupAsync(string instanceName);

    /// <summary>
    /// Every backup the engine holds for an instance, newest first.
    /// </summary>
    /// <remarks>
    /// Each carries the manifest the engine wrote when it captured it — when, how big, and how
    /// consistent the capture was. An instance with no backups is an empty list, not a failure.
    /// </remarks>
    /// <param name="instanceName">Name of the instance</param>
    Task<Result<IReadOnlyList<InstanceBackup>>> GetBackupsAsync(string instanceName);

    /// <summary>
    /// Rolls a backup back onto the instance, replacing what is there.
    /// </summary>
    /// <remarks>
    /// <b>Destructive, and not reversible by this bot.</b> The engine verifies the archive's checksum
    /// before it writes anything, so a corrupt backup is refused rather than half-applied — but what
    /// the instance held beforehand is gone unless it was itself backed up.
    /// </remarks>
    /// <param name="instanceName">Name of the instance</param>
    /// <param name="backupId">The backup's id, as the manifest reports it</param>
    Task<Result> RestoreBackupAsync(string instanceName, string backupId);

    /// <summary>
    /// Sets a single key=value in an instance's .config.ini. kgsm owns the safety
    /// policy and refuses identity/path/structural/toggle keys, surfacing that as a
    /// failed <see cref="Result"/> (the refusal text in <c>Error</c>).
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <param name="key">The config key to set</param>
    /// <param name="value">The new value (may be the empty string)</param>
    /// <returns>Result of the operation</returns>
    Task<Result> SetConfigValueAsync(string instanceName, string key, string value);
}
