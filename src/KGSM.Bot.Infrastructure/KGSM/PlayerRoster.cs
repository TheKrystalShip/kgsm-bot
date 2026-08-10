using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <inheritdoc cref="IPlayerRoster" />
/// <remarks>
/// <para>
/// <b>Two authorities, joined — never one inferred from the other.</b> Run state comes from the
/// engine and presence comes from the supervisor, which is the same status-from-the-authority join
/// every surface in this ecosystem makes. The join matters in one direction especially: a stopped
/// server has nobody on it whatever a stale session map still holds, because a process that is not
/// running has no connections.
/// </para>
/// <para>
/// <b>The supervisor is optional here, like everywhere else in this bot.</b> Unreachable, every
/// server reports <see cref="RosterKnowledge.Unavailable"/> and the rest of the surface carries on.
/// That is a different sentence from "nobody is playing" and is rendered as one.
/// </para>
/// </remarks>
public sealed class PlayerRoster : IPlayerRoster
{
    private readonly IKgsmStateCache _cache;
    private readonly IServerInstanceService _instances;
    private readonly IWatchdogClient _watchdog;
    private readonly ILogger<PlayerRoster> _logger;

    public PlayerRoster(
        IKgsmStateCache cache,
        IServerInstanceService instances,
        IWatchdogClient watchdog,
        ILogger<PlayerRoster> logger)
    {
        _cache = cache;
        _instances = instances;
        _watchdog = watchdog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServerRoster>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Instance> inventory;
        try
        {
            inventory = await _cache.GetInstancesAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The instance inventory could not be read for the player roster.");
            return [];
        }

        IReadOnlyDictionary<string, WatchdogInstancePresence>? presence = await ReadPresenceAsync(cancellationToken);

        // Each check spawns a kgsm process, so they run together rather than in sequence — the answer
        // is as old as the slowest one, not as old as their sum. Same reasoning as the status board.
        return await Task.WhenAll(inventory.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => DescribeAsync(name, presence)));
    }

    /// <inheritdoc />
    public async Task<ServerRoster?> GetAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        Instance? instance;
        try
        {
            instance = await _cache.GetInstanceAsync(instanceName, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The inventory could not be read while looking up {InstanceName}.", instanceName);
            return null;
        }

        if (instance is null)
            return null;

        return await DescribeAsync(instanceName, await ReadPresenceAsync(cancellationToken));
    }

    /// <summary>
    /// The supervisor's presence map, or null when it could not be asked.
    /// </summary>
    /// <remarks>
    /// kgsm-lib already answers an unreachable daemon — and one speaking a shape it cannot read — with
    /// null rather than an exception. The catch is for anything else: this call must never be the
    /// reason a person asking who is online gets an error instead of an answer.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, WatchdogInstancePresence>?> ReadPresenceAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _watchdog.GetPlayerPresenceAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The supervisor could not be asked who is connected.");
            return null;
        }
    }

    /// <summary>
    /// One server's answer, taking the honest states in the order they overrule each other.
    /// </summary>
    /// <remarks>
    /// The order is the point. <b>Stopped comes before observability</b>, because a stopped server has
    /// nobody on it whether or not the game could ever say so — that is a real answer to give about a
    /// game the host cannot otherwise see into. <b>Observability comes before the list</b>, because an
    /// empty list under a game that reports nothing is not a zero.
    /// </remarks>
    private async Task<ServerRoster> DescribeAsync(
        string name, IReadOnlyDictionary<string, WatchdogInstancePresence>? presence)
    {
        Result<bool> active = await _instances.IsActiveAsync(name);

        // Null where the engine could not be asked, which is a third state and not a false. Carried on
        // every answer below so a caller that wants both facts pays for this check once.
        bool? running = active.IsSuccess ? active.Value : null;

        // A run state that could not be read is not a stopped server, so this deliberately does not
        // shortcut to Stopped on a failed check — it falls through to whatever presence can say.
        if (running == false)
            return new ServerRoster(name, RosterKnowledge.Stopped, [], running);

        if (presence is null)
            return new ServerRoster(name, RosterKnowledge.Unavailable, [], running);

        if (!presence.TryGetValue(name, out WatchdogInstancePresence? instance))
        {
            // The supervisor answered but does not carry this instance — installed since it last read
            // the inventory, or an inventory it could not read. Either way it has said nothing about
            // this server, which is not the same as saying nobody is on it.
            return new ServerRoster(name, RosterKnowledge.Unavailable, [], running);
        }

        if (!instance.IsDetected)
            return new ServerRoster(name, RosterKnowledge.NotObservable, [], running);

        RosterPlayer[] players = [.. instance.Players
            .Select(p => new RosterPlayer(p.Name, p.Id))
            .OrderBy(p => p.Label ?? "￿", StringComparer.OrdinalIgnoreCase)];

        return new ServerRoster(name, RosterKnowledge.Known, players, running);
    }
}
