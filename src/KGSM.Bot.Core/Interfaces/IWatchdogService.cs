using KGSM.Bot.Core.Common;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Read-only view onto the kgsm-watchdog supervisor daemon for the bot. This is the
/// <em>observability</em> surface — it reports the supervised state (desired vs.
/// actual liveness, phase, restart streak) that the plain kgsm lifecycle path cannot
/// express. It deliberately does NOT start/stop instances: native lifecycle already
/// routes to the daemon through <c>kgsm.sh</c>, so adding a second write path here
/// would duplicate (and risk drifting from) that routing decision.
/// </summary>
public interface IWatchdogService
{
    /// <summary>
    /// Gets the supervised state of <paramref name="instanceName"/>.
    /// </summary>
    /// <param name="instanceName">The instance to query.</param>
    /// <returns>
    /// A successful result carrying a <see cref="WatchdogStatus"/> — either
    /// <see cref="WatchdogStatus.Supervised"/> with the daemon's state, or
    /// <see cref="WatchdogStatus.NotSupervised"/> when the daemon is up but does not
    /// track this instance (e.g. it was started via the direct path). A failure
    /// result means the daemon is unreachable.
    /// </returns>
    Task<Result<WatchdogStatus>> GetStatusAsync(string instanceName);
}

/// <summary>
/// Outcome of a watchdog status query. Distinguishes "supervised" from "daemon up
/// but not tracking this instance" — both are normal, non-error outcomes (daemon
/// unreachable is reported as a failed <see cref="Result{T}"/> instead).
/// </summary>
public sealed record WatchdogStatus
{
    /// <summary>True when the daemon is currently supervising the instance.</summary>
    public bool IsSupervised { get; private init; }

    /// <summary>The supervised state, present only when <see cref="IsSupervised"/> is true.</summary>
    public WatchdogInstanceState? State { get; private init; }

    /// <summary>The daemon is supervising the instance; carries its reported state.</summary>
    public static WatchdogStatus Supervised(WatchdogInstanceState state) =>
        new() { IsSupervised = true, State = state };

    /// <summary>The daemon is reachable but does not track this instance.</summary>
    public static WatchdogStatus NotSupervised { get; } = new() { IsSupervised = false, State = null };
}
