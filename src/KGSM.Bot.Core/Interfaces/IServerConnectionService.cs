using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Answers "how do I join this server" — address, ports, and whether those ports are actually
/// reachable.
/// </summary>
/// <remarks>
/// Composed from three sources that fail independently: the engine (the instance and its ports), the
/// host (its addresses) and the firewall authority (reachability). A failure of one is reported on
/// the piece it belongs to rather than failing the whole answer, because the ports are still worth
/// having when the external IP could not be read, and the address is still worth having when no
/// firewall answered.
/// </remarks>
public interface IServerConnectionService
{
    /// <summary>
    /// Describe how to reach <paramref name="instanceName"/>. Fails only when the server itself
    /// cannot be read — everything else degrades into the answer.
    /// </summary>
    Task<Result<ServerConnection>> DescribeAsync(
        string instanceName, CancellationToken cancellationToken = default);
}
