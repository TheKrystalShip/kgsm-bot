namespace KGSM.Bot.Discord;

/// <summary>
/// The parts of this bot's job that can stop working while the unit stays active.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every one of these is silence, and silence looks exactly like nothing having happened.</b>
/// That is what makes this leaf different from the others: a monitor that stops sampling serves a
/// stale number somebody can notice, and a bot that stops posting produces an absence nobody can see.
/// </para>
/// <para>
/// Each id is a dedup key, so two spellings of one component would report the same fault twice and
/// recover from neither.
/// </para>
/// </remarks>
public static class BotComponents
{
    /// <summary>The Discord gateway connection.</summary>
    /// <remarks>Nothing is announced and no command reaches this host while it is down.</remarks>
    public const string Gateway = "gateway";

    /// <summary>The store holding which guilds this host is set up in.</summary>
    /// <remarks>
    /// Unopenable means nothing is announced anywhere and <c>/setup</c> refuses, however healthy
    /// everything else reads.
    /// </remarks>
    public const string GuildStore = "guild-store";

    /// <summary>
    /// Whether every configured guild is one the client can currently resolve.
    /// </summary>
    /// <remarks>
    /// Configured, connected, no guild — the state this leaf's status line was written to expose.
    /// One component for all of them, with the offenders in the detail: a component per guild would
    /// grow the dedup set with every server the bot is invited to.
    /// </remarks>
    public const string Guilds = "guilds";

    /// <summary>Whether every bound channel can be resolved.</summary>
    /// <remarks>
    /// A recorded channel the client cannot see is a message that will silently never arrive, and it
    /// looks identical to a working one everywhere else.
    /// </remarks>
    public const string Channels = "channels";

    /// <summary>The outbound queue's back-off.</summary>
    /// <remarks>
    /// The one component here that says the bot is falling behind rather than failing: messages are
    /// late rather than lost. Sustained, it is the throttle that takes the whole surface down.
    /// </remarks>
    public const string SendQueue = "send-queue";
}
