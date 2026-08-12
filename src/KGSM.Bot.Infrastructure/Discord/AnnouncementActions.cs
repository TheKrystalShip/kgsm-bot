using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// Which announcements are worth acting on, and which are worth talking about.
/// </summary>
/// <remarks>
/// The two are not the same set, and treating them as one is the mistake this type exists to keep
/// out of the announcement path.
/// </remarks>
public static class AnnouncementActions
{
    /// <summary>
    /// Whether this announcement carries a restart button.
    /// </summary>
    /// <remarks>
    /// <b>Only a server nobody is already fixing.</b> A crash announcement says the supervisor found
    /// the process dead and is restarting it — a restart button there races the supervisor over the
    /// same server, and whoever pressed it gets the blame for whichever attempt loses. A give-up
    /// announcement is the opposite case: the supervisor has stopped, nothing else is coming, and a
    /// human restart is exactly the next step.
    /// </remarks>
    public static bool OffersRestart(AnnouncementKind kind) => kind == AnnouncementKind.Failed;

    /// <summary>
    /// Whether this announcement opens a thread for the conversation about it.
    /// </summary>
    /// <remarks>
    /// A crash is the announcement people reply to, and a reply in a busy channel scrolls away from
    /// the thing it is about. Both crash kinds get one — being told the supervisor is handling it is
    /// still news somebody will want to talk about.
    /// </remarks>
    public static bool OpensThread(AnnouncementKind kind) =>
        kind is AnnouncementKind.Crashed or AnnouncementKind.Failed;

    /// <summary>
    /// Whether this announcement is worth the assistant investigating before anybody asks.
    /// </summary>
    /// <remarks>
    /// <b>Only a server nobody is coming for.</b> A crash announcement says the supervisor found the
    /// process dead and is restarting it — the server is very likely up again before an investigation
    /// of it finishes, and one per attempt during a crash loop is a thread full of reports about a
    /// problem that is still happening. A give-up is the opposite: the supervisor has stopped, the
    /// server is down until a person acts, and everything needed to explain why is sitting on the host
    /// unread.
    /// </remarks>
    public static bool OpensTriage(AnnouncementKind kind) => kind == AnnouncementKind.Failed;
}
