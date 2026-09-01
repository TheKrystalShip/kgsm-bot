using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace KGSM.Bot.Infrastructure.Cluster;

/// <summary>
/// Where this bot stands in its cluster: the member that carries the chat surface, or a candidate
/// standing by while another does.
/// </summary>
/// <remarks>
/// <para>
/// The capability is what a reader resolves to find the bot at all. A cluster's members are otherwise
/// alike to somebody looking at a roster — one of them is an anchor holding the accounts, one holds
/// the assistant, and without this the one carrying Discord is an anchor holding nothing, which reads
/// as a member waiting to be given a job.
/// </para>
/// <para>
/// <b>Standing by does not stop this bot announcing.</b> Each bot reads the journal of the machine it
/// runs on, so two of them in one cluster describe two different fleets rather than one twice, and
/// silencing the candidate would lose a machine's events entirely. What two bots must not share is a
/// Discord guild — set up in the same server they answer every command twice — and that is somebody's
/// configuration rather than a state the cluster can observe.
/// </para>
/// <para>
/// So there is nothing to reconcile on a standing change, and the base class already holds the
/// standing for whatever asks. A bot on a machine standing alone reports <c>NotClustered</c> and
/// serves everything it always has.
/// </para>
/// </remarks>
public sealed class BotCapabilityWorker(
    ClusterOptions cluster,
    ClusterStateStore state,
    ILogger<BotCapabilityWorker> logger)
    : ClusterCapabilityWorker(ClusterCapability.Bot, cluster, state, logger)
{
    protected override Task OnStandingAsync(CapabilityHolding holding, bool changed, CancellationToken ct) =>
        Task.CompletedTask;
}
