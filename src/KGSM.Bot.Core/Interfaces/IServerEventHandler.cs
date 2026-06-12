using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Interface for handling server events
/// </summary>
public interface IServerEventHandler
{
    /// <summary>
    /// Initializes event handling
    /// </summary>
    void Initialize();

    /// <summary>
    /// Registers a handler for instance installed events
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterInstanceInstalledHandler(Func<string, string, Task> handler);

    /// <summary>
    /// Registers a handler for instance started events
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterInstanceStartedHandler(Func<string, Task> handler);

    /// <summary>
    /// Registers a handler for instance stopped events
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterInstanceStoppedHandler(Func<string, Task> handler);

    /// <summary>
    /// Registers a handler for instance uninstalled events
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterInstanceUninstalledHandler(Func<string, Task> handler);

    /// <summary>
    /// Registers a handler for running status updated events
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterRunningStatusUpdatedHandler(Func<string, InstanceStatus, Task> handler);
}
