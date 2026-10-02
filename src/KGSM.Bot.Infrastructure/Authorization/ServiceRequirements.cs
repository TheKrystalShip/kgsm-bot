using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.ComponentConfig;

// What this bot reads of the engine as its own service account, with nobody asking: the announcements,
// the live status message, the bot's presence line, the per-server channels and the speech recogniser
// all read the servers on this node whether or not anybody has typed anything. Declared for the whole
// assembly because every read of a server's state here is one the bot also makes for itself; a command
// a person types reads the same state after their own check has passed.

[assembly: Requires(KgsmActions.ServerRead, DeclaredScope.Instance,
    "Announce what servers do, keep the live status message and presence current, and read the state of a server it investigates")]
[assembly: Requires(KgsmActions.ServerBackupsRead, DeclaredScope.Instance,
    "Show how recent and how sound each server's last backup is on the live status message")]
[assembly: Requires(KgsmActions.LibraryRead, DeclaredScope.Node,
    "Know the games this node can run, so a server is named by its game and a spoken game name is recognised")]
