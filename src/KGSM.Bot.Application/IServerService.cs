using KGSM.Bot.Core.Interfaces;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Application;

/// <summary>
/// Consolidated service for all server operations — the direct-call replacement for
/// the MediatR command/query dispatcher. Callers inject this instead of IMediator.
/// </summary>
public interface IServerService
{
    // ── Commands ──────────────────────────────────────────────────────────
    Task<OperationResult> StartAsync(string instanceName, CancellationToken ct = default);
    Task<OperationResult> StopAsync(string instanceName, CancellationToken ct = default);
    Task<OperationResult> RestartAsync(string instanceName, CancellationToken ct = default);
    Task<OperationResult> InstallAsync(string blueprintName, string? path, string? version, string? name, CancellationToken ct = default);
    Task<OperationResult> UninstallAsync(string instanceName, CancellationToken ct = default);
    Task<OperationResult> CreateBackupAsync(string instanceName, CancellationToken ct = default);
    Task<OperationResult> UpdateAsync(string instanceName, CancellationToken ct = default);
    Task<OperationResult> SetConfigAsync(string instanceName, string key, string value, CancellationToken ct = default);

    // ── Queries ───────────────────────────────────────────────────────────
    Task<ServerInstancesResult> GetAllInstancesAsync(CancellationToken ct = default);
    Task<BlueprintsResult> GetAllBlueprintsAsync(CancellationToken ct = default);
    Task<ServerStatusResult> GetStatusAsync(string instanceName, CancellationToken ct = default);
    Task<ServerActiveResult> IsActiveAsync(string instanceName, CancellationToken ct = default);
    Task<WatchdogStatusResult> GetWatchdogStatusAsync(string instanceName, CancellationToken ct = default);
}

// ── Result types ─────────────────────────────────────────────────────────────

/// <summary>Result for commands that perform operations but don't return specific data.</summary>
public record OperationResult
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    private OperationResult(bool isSuccess, string? errorMessage)
    {
        IsSuccess = isSuccess;
        ErrorMessage = errorMessage;
    }

    public static OperationResult Success() => new(true, null);
    public static OperationResult Failure(string errorMessage) => new(false, errorMessage);
}

/// <summary>Result for GetAllInstancesAsync.</summary>
public record ServerInstancesResult
{
    public bool IsSuccess { get; }
    public IReadOnlyDictionary<string, Instance>? Instances { get; }
    public string? ErrorMessage { get; }

    private ServerInstancesResult(bool isSuccess, IReadOnlyDictionary<string, Instance>? instances, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Instances = instances;
        ErrorMessage = errorMessage;
    }

    public static ServerInstancesResult Success(IReadOnlyDictionary<string, Instance> instances) =>
        new(true, instances, null);

    public static ServerInstancesResult Failure(string errorMessage) =>
        new(false, null, errorMessage);
}

/// <summary>Result for GetAllBlueprintsAsync.</summary>
public record BlueprintsResult
{
    public bool IsSuccess { get; }
    public IReadOnlyDictionary<string, Blueprint>? Blueprints { get; }
    public string? ErrorMessage { get; }

    private BlueprintsResult(bool isSuccess, IReadOnlyDictionary<string, Blueprint>? blueprints, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Blueprints = blueprints;
        ErrorMessage = errorMessage;
    }

    public static BlueprintsResult Success(IReadOnlyDictionary<string, Blueprint> blueprints) =>
        new(true, blueprints, null);

    public static BlueprintsResult Failure(string errorMessage) =>
        new(false, null, errorMessage);
}

/// <summary>Result for GetStatusAsync.</summary>
public record ServerStatusResult
{
    public bool IsSuccess { get; }
    public string? Status { get; }
    public string? ErrorMessage { get; }

    private ServerStatusResult(bool isSuccess, string? status, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Status = status;
        ErrorMessage = errorMessage;
    }

    public static ServerStatusResult Success(string status) => new(true, status, null);
    public static ServerStatusResult Failure(string errorMessage) => new(false, null, errorMessage);
}

/// <summary>Result for IsActiveAsync.</summary>
public record ServerActiveResult
{
    public bool IsSuccess { get; }
    public bool IsActive { get; }
    public string? ErrorMessage { get; }

    private ServerActiveResult(bool isSuccess, bool isActive, string? errorMessage)
    {
        IsSuccess = isSuccess;
        IsActive = isActive;
        ErrorMessage = errorMessage;
    }

    public static ServerActiveResult Success(bool isActive) => new(true, isActive, null);
    public static ServerActiveResult Failure(string errorMessage) => new(false, false, errorMessage);
}

/// <summary>Result for GetWatchdogStatusAsync.</summary>
public record WatchdogStatusResult
{
    public bool IsSuccess { get; }
    public WatchdogStatus? Status { get; }
    public string? ErrorMessage { get; }

    private WatchdogStatusResult(bool isSuccess, WatchdogStatus? status, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Status = status;
        ErrorMessage = errorMessage;
    }

    public static WatchdogStatusResult Success(WatchdogStatus status) => new(true, status, null);
    public static WatchdogStatusResult Failure(string errorMessage) => new(false, null, errorMessage);
}
