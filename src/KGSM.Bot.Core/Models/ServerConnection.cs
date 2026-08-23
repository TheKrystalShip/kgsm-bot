using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Models;

/// <summary>
/// Where a server's address came from. Which one answered decides how much the address is worth
/// promising: a hostname an operator set is the one they want people to type, a measured external IP
/// is correct right now and can change without notice.
/// </summary>
public enum AddressSource
{
    /// <summary>The public address the operator configured. Authoritative — use it verbatim.</summary>
    Configured,

    /// <summary>The host's external IP, as the host measured it. Correct now; not a promise.</summary>
    Measured,

    /// <summary>Neither answered. The address is unknown and is reported as unknown.</summary>
    None,
}

/// <summary>
/// The addresses this host is reached at.
/// </summary>
/// <param name="Public">What to type to reach it from outside, or <see langword="null"/> when
/// neither a configured hostname nor a measured external IP answered.</param>
/// <param name="Source">Which of those answered.</param>
/// <param name="Local">The host's own addresses, for somebody on the same network.</param>
public sealed record HostAddresses(
    string? Public,
    AddressSource Source,
    IReadOnlyList<string> Local)
{
    /// <summary>Nothing could be read at all.</summary>
    public static HostAddresses Unknown { get; } = new(null, AddressSource.None, []);
}

/// <summary>
/// Whether a server's ports are actually reachable from outside, as far as the firewall authority can
/// say. This is the second half of "is it up" and the half nothing else answers.
/// </summary>
/// <remarks>
/// <b>Every value here is measured or is an admission.</b> The distinction that matters most is
/// <see cref="Unfiltered"/> versus <see cref="Closed"/>: a backend that is installed but not enforcing
/// filters nothing, so its empty rule set means every port is open — reading it as "closed" is the
/// single most tempting way to fabricate this verdict. <see cref="Unknown"/> and
/// <see cref="Unavailable"/> are the two ways of not knowing, and neither is ever collapsed into a
/// negative answer.
/// </remarks>
public enum PortExposure
{
    /// <summary>The firewall is enforcing and holds a rule for every port this server uses.</summary>
    Open,

    /// <summary>The firewall is enforcing and holds rules for some of this server's ports.</summary>
    Partial,

    /// <summary>The firewall is enforcing and holds no rule for this server.</summary>
    Closed,

    /// <summary>A backend is present but not enforcing, so nothing is filtered and every port is
    /// reachable. Not the same fact as <see cref="Open"/>, and not remotely the same as
    /// <see cref="Closed"/>.</summary>
    Unfiltered,

    /// <summary>The authority answered that it cannot tell. An honest unknown, never "closed".</summary>
    Unknown,

    /// <summary>No firewall authority is reachable from here. The bot runs fine without one; it just
    /// has nothing to say about reachability.</summary>
    Unavailable,
}

/// <summary>
/// What the firewall authority knows about one server's ports.
/// </summary>
/// <param name="Exposure">The verdict.</param>
/// <param name="Backend">The backend that answered (<c>ufw</c>, <c>none</c>), or
/// <see langword="null"/> when nothing answered.</param>
/// <param name="OpenPorts">The ports the authority holds open for this server. Meaningful only when
/// <see cref="PortExposure.Open"/> or <see cref="PortExposure.Partial"/>; empty otherwise, and an
/// empty list is never itself the verdict.</param>
public sealed record FirewallExposure(
    PortExposure Exposure,
    string? Backend = null,
    IReadOnlyList<PortMapping>? OpenPorts = null)
{
    /// <summary>Nothing to ask: no authority is reachable from this host.</summary>
    public static FirewallExposure Unavailable { get; } = new(PortExposure.Unavailable);
}

/// <summary>
/// Everything needed to answer "how do I join this server" — the question a game Discord asks more
/// than any other, and the one this host can answer twice over: the address and ports it is served
/// on, and whether those ports are actually reachable.
/// </summary>
/// <remarks>
/// Each piece is separately unknown-able and each says so on its own. A server whose address cannot
/// be determined still reports its ports; a host with no firewall authority still reports its
/// address. Nothing here is filled in with a plausible value to keep the shape tidy.
/// </remarks>
/// <param name="Instance">The server, as kgsm names it.</param>
/// <param name="Address">What to type to reach it, or <see langword="null"/> when neither a
/// configured hostname nor a measured external IP answered.</param>
/// <param name="AddressSource">Which of those answered.</param>
/// <param name="LocalAddresses">The host's own addresses, for somebody on the same network.</param>
/// <param name="Ports">The server's ports, canonical and range-preserving, straight from the
/// engine.</param>
/// <param name="Firewall">What the firewall authority says about those ports.</param>
/// <param name="IsRunning">Whether the server is up, or <see langword="null"/> when that could not
/// be read. A closed-looking port on a stopped server is simply a stopped server, and the reply says
/// which it is — but only when it knows. ⚠ Null is not <see langword="false"/>: a footer saying
/// nothing is listening, printed because a read failed, is the fabrication this three-state
/// prevents.</param>
public sealed record ServerConnection(
    string Instance,
    string? Address,
    AddressSource AddressSource,
    IReadOnlyList<string> LocalAddresses,
    IReadOnlyList<PortMapping> Ports,
    FirewallExposure Firewall,
    bool? IsRunning);
