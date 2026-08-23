using TheKrystalShip.KGSM.Core.Models.Enums;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Who is playing on this host, answered honestly per server.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place the count comes from.</b> The <c>/players</c> command, the live status board and
/// anything else that wants a player number read this — two surfaces deriving a roster separately is
/// two numbers that can disagree in front of the same person.
/// </para>
/// <para>
/// <b>Three facts have to agree before a number means anything</b>, and they come from three places:
/// whether the server is running (the engine), whether its players can be observed at all (the
/// supervisor), and who is connected (the supervisor's live map). Any one of them missing makes the
/// count unknown rather than zero — which is the whole reason this is a service and not a call.
/// </para>
/// </remarks>
public interface IPlayerRoster
{
    /// <summary>
    /// The roster for every installed server, keyed by name, ordered by name.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ServerRoster>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The roster for one server, or <see langword="null"/> when this host has no such server.
    /// </summary>
    /// <param name="instanceName">The server, as kgsm names it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ServerRoster?> GetAsync(string instanceName, CancellationToken cancellationToken = default);
}

/// <summary>
/// One server's answer to "who is playing", and the run state that answer was decided against.
/// </summary>
/// <param name="Server">The server, as kgsm names it.</param>
/// <param name="Knowledge">What can honestly be said about it.</param>
/// <param name="Players">
/// Who is connected. Non-empty only when <paramref name="Knowledge"/> is
/// <see cref="RosterKnowledge.Known"/> — every other state carries no players, because a list under
/// them would be a claim this host cannot make.
/// </param>
/// <param name="Running">
/// Whether the server is up, or <see langword="null"/> when that could not be read.
/// </param>
/// <param name="LibraryState">
/// Where the server's files stand relative to the library holding them, or <see langword="null"/>
/// when the engine did not say. This is the field that explains a null <paramref name="Running"/>:
/// <see cref="InstanceLibraryState.Offline"/> means the disk is away, which is why nothing about the
/// server could be measured.
/// </param>
/// <param name="Library">
/// The library the server is placed in, so a surface reporting an unreachable one can name the disk
/// somebody has to plug back in. Empty when the engine did not say.
/// </param>
/// <remarks>
/// <para>
/// <b>Run state is carried rather than re-read.</b> Deciding whether a roster means anything requires
/// asking the engine whether the server is even running, and that answer costs a kgsm process per
/// server. A surface that wants both — the live status board wants exactly both — would otherwise ask
/// the same question a second time, paying twice for one fact and able to get two answers about the
/// same moment.
/// </para>
/// <para>
/// <b>The library state is carried for the same reason.</b> It is what turns a bare "unknown" into a
/// sentence worth reading, and a surface deriving it from a second inventory read would be answering
/// about a different moment than the run state beside it.
/// </para>
/// </remarks>
public sealed record ServerRoster(
    string Server,
    RosterKnowledge Knowledge,
    IReadOnlyList<RosterPlayer> Players,
    bool? Running,
    InstanceLibraryState? LibraryState = null,
    string Library = "")
{
    /// <summary>
    /// Whether the server's files are out of reach, which is why the rest of this answer is empty.
    /// </summary>
    public bool LibraryAway => LibraryState == InstanceLibraryState.Offline;

    /// <summary>
    /// How many are connected, or <see langword="null"/> when that is not known.
    /// </summary>
    /// <remarks>
    /// <b>Null and 0 are different answers and must render differently.</b> This is the property a
    /// caller should reach for rather than <c>Players.Count</c>, which is 0 for every state and would
    /// quietly turn "nobody can tell" into "nobody is here".
    /// </remarks>
    public int? Count => Knowledge == RosterKnowledge.Known ? Players.Count : null;
}

/// <summary>What this host can honestly say about a server's players.</summary>
public enum RosterKnowledge
{
    /// <summary>
    /// The roster is a measurement. An empty list here genuinely means nobody is connected.
    /// </summary>
    Known,

    /// <summary>
    /// The server is not running, so nobody is on it. Reported as its own state rather than as a
    /// count of zero — "stopped" is what a person actually wants to be told, and it is knowable even
    /// for a game whose players could never be observed.
    /// </summary>
    Stopped,

    /// <summary>
    /// The game does not report its players to this host — no log patterns it can match, no RCON it
    /// can poll. <b>Never a zero.</b> The server may be full.
    /// </summary>
    NotObservable,

    /// <summary>
    /// The supervisor could not be asked, so nothing is known about anybody. Distinct from
    /// <see cref="NotObservable"/>: that one is a settled fact about the game, this one is a
    /// temporary failure of this host to look.
    /// </summary>
    Unavailable,
}

/// <summary>
/// One connected player. Every field is optional because the sources genuinely differ — a game may
/// give a name and no id, an id and no name, or an address and neither.
/// </summary>
/// <param name="Name">The display name, when the game reports one.</param>
/// <param name="Id">The account or platform id, when the game reports one.</param>
public sealed record RosterPlayer(string? Name, string? Id)
{
    /// <summary>
    /// What to call this player, or <see langword="null"/> when the game gave nothing to call them.
    /// </summary>
    /// <remarks>
    /// The network address is deliberately not a fallback: it identifies a connection rather than a
    /// person, and putting one in a chat message publishes a player's IP to everyone in the channel.
    /// A session with no name and no id is counted and left unnamed.
    /// </remarks>
    public string? Label =>
        !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : !string.IsNullOrWhiteSpace(Id) ? Id.Trim()
        : null;
}
