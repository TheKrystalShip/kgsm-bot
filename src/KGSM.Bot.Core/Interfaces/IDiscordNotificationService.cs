using KGSM.Bot.Core.Common;

using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Interface for Discord notification service
/// </summary>
public interface IDiscordNotificationService
{
    /// <summary>
    /// Notifies users via Discord about a server's running status update
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <param name="status">Whether the server is active (true) or inactive (false)</param>
    /// <returns>Result of the notification operation</returns>
    Task<Result> NotifyRunningStatusUpdatedAsync(string instanceName, InstanceStatus status);
}
