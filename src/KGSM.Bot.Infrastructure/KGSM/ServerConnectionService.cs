using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Composes the connection answer from the engine, the host and the firewall authority.
/// </summary>
/// <remarks>
/// <b>The ports come from the engine, whole.</b> <see cref="Instance.Ports"/> is already the
/// canonical range-preserving form, so nothing here re-parses a port spec or invents a default port
/// for a game whose blueprint declares none — a server with no declared ports reports none.
/// </remarks>
public sealed class ServerConnectionService : IServerConnectionService
{
    private readonly IKgsmStateCache _cache;
    private readonly IServerInstanceService _instances;
    private readonly IHostAddressService _addresses;
    private readonly IFirewallReport _firewall;
    private readonly ILogger<ServerConnectionService> _logger;

    public ServerConnectionService(
        IKgsmStateCache cache,
        IServerInstanceService instances,
        IHostAddressService addresses,
        IFirewallReport firewall,
        ILogger<ServerConnectionService> logger)
    {
        _cache = cache;
        _instances = instances;
        _addresses = addresses;
        _firewall = firewall;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ServerConnection>> DescribeAsync(
        string instanceName, CancellationToken cancellationToken = default)
    {
        Instance? instance;
        try
        {
            instance = await _cache.GetInstanceAsync(instanceName, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not read the instance {InstanceName}.", instanceName);
            return Result.Failure<ServerConnection>($"could not read {instanceName}: {e.Message}");
        }

        if (instance is null)
            return Result.Failure<ServerConnection>($"there is no server called '{instanceName}' on this host");

        IReadOnlyList<PortMapping> ports = instance.Ports ?? [];

        // The three remaining reads are independent and each is allowed to come back empty-handed:
        // an unreadable external IP does not cost the ports, and an absent firewall does not cost
        // the address.
        HostAddresses addresses = await _addresses.ResolveAsync(cancellationToken);
        FirewallExposure firewall = await _firewall.DescribeAsync(instanceName, ports, cancellationToken);
        bool? running = await IsRunningAsync(instanceName, instance);

        return Result.Success(new ServerConnection(
            Instance: instance.Name,
            Address: addresses.Public,
            AddressSource: addresses.Source,
            LocalAddresses: addresses.Local,
            Ports: ports,
            Firewall: firewall,
            IsRunning: running));
    }

    /// <summary>
    /// Whether the server is up, or null when this host cannot say.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>An instance whose library is away is not asked about.</b> The engine answers this by exit
    /// code, so it has no way to say "I could not look" and returns the stopped one for an instance
    /// it cannot open — which would put "nothing is listening on those ports" under a connect string
    /// on the strength of an unplugged disk.
    /// </remarks>
    private async Task<bool?> IsRunningAsync(string instanceName, Instance instance)
    {
        if (instance.LibraryState == InstanceLibraryState.Offline)
            return null;

        Result<bool> active = await _instances.IsActiveAsync(instanceName);
        return active.IsSuccess ? active.Value : null;
    }
}
