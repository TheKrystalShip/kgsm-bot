using TheKrystalShip.KGSM.Auth.Access;

namespace KGSM.Bot.Infrastructure.Authorization;

/// <summary>
/// The actions this bot performs itself, and the assistant's it names when it asks on somebody's
/// behalf.
/// </summary>
/// <remarks>
/// The engine's actions are <c>TheKrystalShip.KGSM.KgsmActions</c>: a command that starts a server checks
/// <c>kgsm:server.start</c>, the same action the Control Panel and the assistant check for it. Only what
/// is this bot's own lives here.
/// </remarks>
public static class BotActions
{
    /// <summary>The component half of this bot's action ids, and of its service account's name.</summary>
    public const string Component = "bot";

    /// <summary>
    /// Where this bot's action manifest is installed, beside its config descriptor: the node on this
    /// machine reports every file in that directory, and this bot reports this one as its own.
    /// </summary>
    public const string ActionManifest = "/var/lib/kgsm/leaves/actions/bot.json";

    /// <summary>See whether the bot and what it depends on are answering.</summary>
    public const string StatusRead = "bot:status.read";

    /// <summary>Choose which Discord servers and channels this bot announces into, and what it follows.</summary>
    public const string AnnouncementsManage = "bot:announcements.manage";

    /// <summary>Have the bot join a voice channel and listen.</summary>
    public const string VoiceUse = "bot:voice.use";

    /// <summary>Talk to the assistant: every active person holds it.</summary>
    public const string AssistantChat = "assistant:chat";

    /// <summary>Clear a conversation the assistant shares with a whole channel.</summary>
    public const string AssistantClearShared = "assistant:conversations.clear-shared";

    /// <summary>
    /// The handle this bot acts under for work nobody asked it for — its own service account on the
    /// cluster member it is.
    /// </summary>
    public static string ServiceHandle(string member) => new ServiceIdentity(Component, member).Actor;
}
