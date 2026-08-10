using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// The address this host is reached at.
/// </summary>
/// <remarks>
/// One place, because two surfaces hand it out — the connection answer and the live status message —
/// and two derivations of the same address are two answers that can disagree with each other in front
/// of the same people.
/// </remarks>
public interface IHostAddressService
{
    /// <summary>
    /// Resolve the address to publish. Never throws: a host that cannot read its own external
    /// address says so with <see cref="AddressSource.None"/>.
    /// </summary>
    Task<HostAddresses> ResolveAsync(CancellationToken cancellationToken = default);
}
