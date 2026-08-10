using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Resolves the address this host publishes: the operator's, else the one the host measured.
/// </summary>
/// <remarks>
/// <b>An operator-set address wins.</b> A host cannot discover the name people actually type — a DNS
/// record pointing at it is a fact about the world, not about the machine — so when the operator has
/// said what the address is, that is the address. The measured external IP is the fallback, and it is
/// exactly as good as the moment it was read, which is why the surfaces that print it say so.
/// </remarks>
public sealed class HostAddressService : IHostAddressService
{
    private readonly IServerInstanceService _instances;
    private readonly DiscordOptions _options;
    private readonly ILogger<HostAddressService> _logger;

    public HostAddressService(
        IServerInstanceService instances,
        IOptions<DiscordOptions> options,
        ILogger<HostAddressService> logger)
    {
        _instances = instances;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HostAddresses> ResolveAsync(CancellationToken cancellationToken = default)
    {
        string? configured = string.IsNullOrWhiteSpace(_options.PublicAddress)
            ? null
            : _options.PublicAddress.Trim();

        Result<SystemInfo> system;
        try
        {
            system = await _instances.GetSystemInfoAsync();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Host addresses could not be read.");
            return configured is null
                ? HostAddresses.Unknown
                : new HostAddresses(configured, AddressSource.Configured, []);
        }

        if (system.IsFailure)
        {
            _logger.LogDebug("Host addresses were unreadable: {Error}", system.Error);
            return configured is null
                ? HostAddresses.Unknown
                : new HostAddresses(configured, AddressSource.Configured, []);
        }

        SystemNetworkInfo network = system.Value!.Network;
        IReadOnlyList<string> local = network.LocalIps ?? [];

        if (configured is not null)
            return new HostAddresses(configured, AddressSource.Configured, local);

        return string.IsNullOrWhiteSpace(network.ExternalIp)
            ? new HostAddresses(null, AddressSource.None, local)
            : new HostAddresses(network.ExternalIp, AddressSource.Measured, local);
    }
}
