using System.Net.Http;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// <see cref="IWatchdogService"/> backed by kgsm-lib's typed
/// <see cref="IWatchdogClient"/> (HTTP/1.1 over the daemon's unix socket). Translates
/// the client's transport-level outcomes into the bot's <see cref="Result{T}"/>:
/// a tracked instance and an untracked-but-reachable daemon are both successes
/// (<see cref="WatchdogStatus"/>); only an unreachable daemon is a failure.
/// </summary>
public class WatchdogService : IWatchdogService
{
    private readonly IWatchdogClient _watchdogClient;
    private readonly ILogger<WatchdogService> _logger;

    public WatchdogService(
        IWatchdogClient watchdogClient,
        ILogger<WatchdogService> logger)
    {
        _watchdogClient = watchdogClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<WatchdogStatus>> GetStatusAsync(string instanceName)
    {
        try
        {
            _logger.LogDebug("Querying watchdog supervision state for {InstanceName}", instanceName);

            // null == HTTP 404: the daemon is up but isn't tracking this instance
            // (e.g. it was started via the direct path). That's a normal answer, not
            // an error — surface it as NotSupervised.
            var state = await _watchdogClient.GetStatusAsync(instanceName);
            if (state is null)
            {
                _logger.LogDebug("Watchdog does not supervise {InstanceName}", instanceName);
                return Result.Success(WatchdogStatus.NotSupervised);
            }

            _logger.LogDebug("Watchdog supervises {InstanceName}: phase={Phase}, restarts={Restarts}",
                instanceName, state.Phase, state.Restarts);
            return Result.Success(WatchdogStatus.Supervised(state));
        }
        catch (HttpRequestException ex)
        {
            // The connection itself failed — the daemon is down or its socket is
            // gone. This is the one genuine failure: the bot cannot tell whether the
            // instance is supervised, so don't pretend it isn't.
            _logger.LogWarning(ex, "Watchdog daemon unreachable while querying {InstanceName}", instanceName);
            return Result.Failure<WatchdogStatus>("The kgsm-watchdog daemon is not reachable.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying watchdog supervision state for {InstanceName}", instanceName);
            return Result.Failure<WatchdogStatus>(ex.Message);
        }
    }
}
