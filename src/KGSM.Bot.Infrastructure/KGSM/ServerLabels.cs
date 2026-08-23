using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// <see cref="IServerLabels"/> over the cached inventory.
/// </summary>
/// <remarks>
/// The inventory is already cached and dropped on the engine's own install, uninstall and rename
/// events, so a label lookup is a dictionary read on the announcement path rather than a kgsm
/// process per message. Every failure — an unknown server, an inventory that could not be read —
/// answers with the id, because a message that names no server is worse than one naming it the way
/// the machine does.
/// </remarks>
public sealed class ServerLabels : IServerLabels
{
    private readonly IKgsmStateCache _cache;
    private readonly ILogger<ServerLabels> _logger;

    public ServerLabels(IKgsmStateCache cache, ILogger<ServerLabels> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> LabelAsync(string instanceId, CancellationToken cancellationToken = default) =>
        ServerLabel.Of(instanceId, await DisplayNameAsync(instanceId, cancellationToken));

    /// <inheritdoc />
    public async Task<string> DescribeAsync(string instanceId, CancellationToken cancellationToken = default) =>
        ServerLabel.Describe(instanceId, await DisplayNameAsync(instanceId, cancellationToken));

    private async Task<string?> DisplayNameAsync(string instanceId, CancellationToken cancellationToken)
    {
        try
        {
            Instance? instance = await _cache.GetInstanceAsync(instanceId, cancellationToken);
            return instance?.DisplayName;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "The label for {InstanceId} could not be read; using the id.", instanceId);
            return null;
        }
    }
}
