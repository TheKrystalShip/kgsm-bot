using KGSM.Bot.Core.Models;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Asks the host's firewall authority whether a server's ports are actually reachable.
/// </summary>
/// <remarks>
/// <b>The authority is optional.</b> kgsm-firewall is a sibling leaf, and the bot must work with it
/// absent — so an unreachable authority is <see cref="FirewallExposure.Unavailable"/>, which reads as
/// "nothing to say about reachability", never as a failure and never as "closed".
/// </remarks>
public interface IFirewallReport
{
    /// <summary>
    /// What the authority says about <paramref name="instanceName"/>'s <paramref name="ports"/>.
    /// Never throws: every way of not knowing has a value on <see cref="PortExposure"/>.
    /// </summary>
    Task<FirewallExposure> DescribeAsync(
        string instanceName,
        IReadOnlyList<PortMapping> ports,
        CancellationToken cancellationToken = default);
}
