using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Interface for handling server events
/// </summary>
/// <remarks>
/// Two kinds of subscription, because the bot reacts to an engine event in two unrelated ways.
/// <see cref="RegisterAnnouncementHandler"/> is the reporting side: one callback for every event
/// worth telling a channel about, each already reduced to a <see cref="ServerAnnouncement"/>, and
/// each individually switchable by the operator. The two lifecycle registrations below are the
/// bookkeeping side — creating a channel, dropping a cached inventory — which has to happen whether
/// or not anything is announced, so it does not pass through the toggles.
/// </remarks>
public interface IServerEventHandler
{
    /// <summary>
    /// Initializes event handling
    /// </summary>
    void Initialize();

    /// <summary>
    /// Registers a handler called once per announceable event, with the announcement built from it.
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterAnnouncementHandler(Func<ServerAnnouncement, Task> handler);

    /// <summary>
    /// Registers a handler for instance installed events. Independent of the announcement
    /// toggles: this is what gives a newly installed server its channel.
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterInstanceInstalledHandler(Func<string, string, Task> handler);

    /// <summary>
    /// Registers a handler for instance uninstalled events. Independent of the announcement
    /// toggles: this is what retires the server's channel and its cached inventory.
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterInstanceUninstalledHandler(Func<string, Task> handler);

    /// <summary>
    /// Registers a handler called when a server's display name changes, with its id and its new
    /// label. Bookkeeping only, and there is deliberately no announcement kind behind it: renaming
    /// changes what every surface calls the server and nothing about the server itself.
    /// </summary>
    /// <param name="handler">The handler function</param>
    void RegisterInstanceRenamedHandler(Func<string, string, Task> handler);
}
