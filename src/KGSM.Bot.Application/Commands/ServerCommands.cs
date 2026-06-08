using MediatR;

namespace KGSM.Bot.Application.Commands;

/// <summary>
/// Command to start a server instance
/// </summary>
public record StartServerCommand(string InstanceName) : IRequest<OperationResult>;

/// <summary>
/// Command to stop a server instance
/// </summary>
public record StopServerCommand(string InstanceName) : IRequest<OperationResult>;

/// <summary>
/// Command to restart a server instance
/// </summary>
public record RestartServerCommand(string InstanceName) : IRequest<OperationResult>;

/// <summary>
/// Command to install a server instance
/// </summary>
public record InstallServerCommand(
    string BlueprintName,
    string? InstallPath = null,
    string? Version = null,
    string? Name = null) : IRequest<OperationResult>;

/// <summary>
/// Command to uninstall a server instance
/// </summary>
public record UninstallServerCommand(string InstanceName) : IRequest<OperationResult>;

/// <summary>
/// Command to create a server instance backup
/// </summary>
public record CreateBackupCommand(string InstanceName) : IRequest<OperationResult>;

/// <summary>
/// Command to update a server instance to the latest version
/// </summary>
public record UpdateServerCommand(string InstanceName) : IRequest<OperationResult>;

/// <summary>
/// Result for commands that perform operations but don't return specific data
/// </summary>
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
