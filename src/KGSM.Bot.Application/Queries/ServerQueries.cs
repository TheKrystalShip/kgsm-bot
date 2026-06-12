using KGSM.Bot.Core.Interfaces;

using MediatR;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Application.Queries;

/// <summary>
/// Query to get all server instances
/// </summary>
public record GetAllInstancesQuery() : IRequest<ServerInstancesResult>;

/// <summary>
/// Result for the GetAllInstancesQuery
/// </summary>
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

/// <summary>
/// Query to get all blueprints
/// </summary>
public record GetAllBlueprintsQuery() : IRequest<BlueprintsResult>;

/// <summary>
/// Result for the GetAllBlueprintsQuery
/// </summary>
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

/// <summary>
/// Query to get server status
/// </summary>
public record GetServerStatusQuery(string InstanceName) : IRequest<ServerStatusResult>;

/// <summary>
/// Result for the GetServerStatusQuery
/// </summary>
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

/// <summary>
/// Query to check if a server is active
/// </summary>
public record IsServerActiveQuery(string InstanceName) : IRequest<ServerActiveResult>;

/// <summary>
/// Result for the IsServerActiveQuery
/// </summary>
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

/// <summary>
/// Query to get the Discord channel ID for an instance
/// </summary>
public record GetInstanceChannelIdQuery(string InstanceName) : IRequest<InstanceChannelIdResult>;

/// <summary>
/// Result for the GetInstanceChannelIdQuery
/// </summary>
public record InstanceChannelIdResult
{
    public bool IsSuccess { get; }
    public ulong? ChannelId { get; }
    public string? ErrorMessage { get; }

    private InstanceChannelIdResult(bool isSuccess, ulong? channelId, string? errorMessage)
    {
        IsSuccess = isSuccess;
        ChannelId = channelId;
        ErrorMessage = errorMessage;
    }

    public static InstanceChannelIdResult Success(ulong? channelId) => new(true, channelId, null);
    public static InstanceChannelIdResult Failure(string errorMessage) => new(false, null, errorMessage);
}

/// <summary>
/// Query for the kgsm-watchdog supervision state of an instance (desired vs. actual
/// liveness, phase, restart streak) — the observability surface the plain lifecycle
/// path can't express.
/// </summary>
public record GetWatchdogStatusQuery(string InstanceName) : IRequest<WatchdogStatusResult>;

/// <summary>
/// Result for the <see cref="GetWatchdogStatusQuery"/>. <see cref="Status"/> carries
/// the supervised-vs-not-supervised distinction; a failure means the daemon was
/// unreachable.
/// </summary>
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
