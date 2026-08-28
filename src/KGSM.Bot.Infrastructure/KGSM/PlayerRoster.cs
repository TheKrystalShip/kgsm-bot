using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

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
        return await Task.WhenAll(inventory
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => DescribeAsync(pair.Key, pair.Value, presence)));
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

        return await DescribeAsync(instanceName, instance, await ReadPresenceAsync(cancellationToken));
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
    /// <para>
    /// The order is the point. <b>An unreachable library comes before everything</b>, because it is
    /// the one state where no measurement is possible at all. <b>Stopped comes before
    /// observability</b>, because a stopped server has nobody on it whether or not the game could
    /// ever say so — that is a real answer to give about a game the host cannot otherwise see into.
    /// <b>Observability comes before the list</b>, because an empty list under a game that reports
    /// nothing is not a zero.
    /// </para>
    /// <para>
    /// <b>A server whose library is away is not asked about, rather than asked and disbelieved.</b>
    /// The engine's <c>is-active</c> answers by exit code, so it has only two values and returns the
    /// stopped one for an instance it cannot even open — which is how an unplugged disk comes to be
    /// reported as fifteen servers going down at once. The supervisor's presence map can likewise
    /// still hold an entry from before the library went away, and an empty player list under it would
    /// read as a measured zero. Both are refused here, at the one place that knows the library state
    /// and the run state together.
    /// </para>
    /// </remarks>
    private async Task<ServerRoster> DescribeAsync(
        string name, Instance instance, IReadOnlyDictionary<string, WatchdogInstancePresence>? presence)
    {
        if (instance.LibraryState == InstanceLibraryState.Offline)
        {
            return new ServerRoster(
                name, RosterKnowledge.Unavailable, [], Running: null,
                instance.LibraryState, instance.Library);
        }

        Result<bool> active = await _instances.IsActiveAsync(name);

        // Null where the engine could not be asked, which is a third state and not a false. Carried on
        // every answer below so a caller that wants both facts pays for this check once.
        bool? running = active.IsSuccess ? active.Value : null;

        ServerRoster Answer(RosterKnowledge knowledge, IReadOnlyList<RosterPlayer> players) =>
            new(name, knowledge, players, running, instance.LibraryState, instance.Library);

        // A run state that could not be read is not a stopped server, so this deliberately does not
        // shortcut to Stopped on a failed check — it falls through to whatever presence can say.
        if (running == false)
            return Answer(RosterKnowledge.Stopped, []);

        if (presence is null)
            return Answer(RosterKnowledge.Unavailable, []);

        if (!presence.TryGetValue(name, out WatchdogInstancePresence? observed))
        {
            // The supervisor answered but does not carry this instance — installed since it last read
            // the inventory, or an inventory it could not read. Either way it has said nothing about
            // this server, which is not the same as saying nobody is on it.
            return Answer(RosterKnowledge.Unavailable, []);
        }

        if (!observed.IsDetected)
            return Answer(RosterKnowledge.NotObservable, []);

        RosterPlayer[] players = [.. observed.Players
            .Select(p => new RosterPlayer(p.Name, p.Id))
            .OrderBy(p => p.Label ?? "￿", StringComparer.OrdinalIgnoreCase)];

        return Answer(RosterKnowledge.Known, players);
    }
}
