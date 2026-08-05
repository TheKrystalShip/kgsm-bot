using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Interface for Discord notification service
/// </summary>
public interface IDiscordNotificationService
{
    /// <summary>
    /// Posts an announcement to the channel that reports on its server, when the operator has that
    /// kind of announcement switched on.
    /// </summary>
    /// <param name="announcement">What happened</param>
    /// <returns>
    /// Success once the message is posted, and success when the announcement's kind is switched off
    /// — a suppressed announcement is the configured outcome, not a failure. Failure when the
    /// message was meant to be posted and could not be.
    /// </returns>
    Task<Result> AnnounceAsync(ServerAnnouncement announcement);
}
