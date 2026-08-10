using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Reads the host's firewall authority through kgsm-lib's client and reduces its answer to one
/// verdict about one server.
/// </summary>
/// <remarks>
/// <para>
/// <b>The authority is a sibling leaf and stays optional.</b> Nothing about this bot needs a firewall
/// to exist: an unreachable socket is <see cref="PortExposure.Unavailable"/> and the surface that
/// asked simply has nothing to say about reachability. That is what "a leaf may consume a sibling"
/// means here — an enhancement when it is there, silence when it is not.
/// </para>
/// <para>
/// <b>An inactive backend is not a closed one.</b> A firewall that is installed but not enforcing
/// filters nothing, so its empty rule set means every port is reachable. Reading that set as "closed"
/// is the one mistake this whole type exists to avoid, and kgsm-lib reports enforcement separately
/// from the rules precisely so it can be avoided.
/// </para>
/// </remarks>
public sealed class FirewallReport : IFirewallReport
{
    private readonly IFirewallService _firewall;
    private readonly ILogger<FirewallReport> _logger;

    public FirewallReport(IFirewallService firewall, ILogger<FirewallReport> logger)
    {
        _firewall = firewall;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FirewallExposure> DescribeAsync(
        string instanceName,
        IReadOnlyList<PortMapping> ports,
        CancellationToken cancellationToken = default)
    {
        FirewallBackendInfo backend;
        FirewallListResult listed;

        try
        {
            // The backend call doubles as the reachability probe: if the authority is not there, this
            // is where it says so, and nothing below has to distinguish absent from unhelpful.
            backend = await _firewall.BackendAsync(cancellationToken);
            listed = await _firewall.ListOwnedAsync(instanceName, cancellationToken);
        }
        catch (Exception e)
        {
            // Unreachable, not broken. Logged at debug because a host with no firewall leaf is a
            // supported configuration, not a fault worth a warning on every command.
            _logger.LogDebug(e,
                "The firewall authority did not answer for {InstanceName}; reporting reachability as unavailable.",
                instanceName);
            return FirewallExposure.Unavailable;
        }

        string? name = string.IsNullOrWhiteSpace(backend.Backend) ? null : backend.Backend;

        // Enforcement is read before the rules, because it decides what the rules mean.
        if (listed.Enforcement == FirewallEnforcement.Inactive)
            return new FirewallExposure(PortExposure.Unfiltered, name);

        if (listed.Status != FirewallListStatus.Ok || listed.Enforcement == FirewallEnforcement.Unknown)
            return new FirewallExposure(PortExposure.Unknown, name);

        List<PortMapping> open = [.. listed.Rules
            .Where(rule => string.Equals(rule.Instance, instanceName, StringComparison.Ordinal))
            .SelectMany(rule => rule.Ports)];

        // Compared port by port rather than mapping by mapping: the authority is free to hold the
        // same ports as a differently-shaped set of ranges, and a range-shape difference is not a hole.
        HashSet<(int, string)> held = [.. open.Expand().Select(Normalize)];
        (int, string)[] wanted = [.. ports.Expand().Select(Normalize)];

        // A server that declares no ports has nothing to be open or closed. Saying "closed" about an
        // empty set would read as a problem where there is not one.
        if (wanted.Length == 0)
            return new FirewallExposure(PortExposure.Unknown, name);

        int covered = wanted.Count(held.Contains);

        PortExposure exposure = covered == wanted.Length
            ? PortExposure.Open
            : covered == 0 ? PortExposure.Closed : PortExposure.Partial;

        return new FirewallExposure(exposure, name, open);
    }

    private static (int Port, string Protocol) Normalize((int Port, string Protocol) entry) =>
        (entry.Port, entry.Protocol.ToLowerInvariant());
}
