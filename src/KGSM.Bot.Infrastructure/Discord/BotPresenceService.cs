using System.Text;

using Discord;
using Discord.WebSocket;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.Discord;

/// <inheritdoc cref="IBotPresence" />
/// <remarks>
/// <para>
/// <b>A fixed cadence, and nothing else touches it.</b> The loop reads the host, composes a line, and
/// sends it only when it differs from the one already showing — a quiet host costs one read and no
/// gateway traffic at all. Nothing here subscribes to an event: the whole point of a presence is that
/// it is cheap, and an event-driven one is a burst waiting for a host reboot.
/// </para>
/// <para>
/// <b>Not through <see cref="IDiscordSendQueue"/>, and that is not an oversight.</b> The queue paces
/// REST calls against Discord's HTTP buckets; a presence update is a gateway op with its own limit,
/// so putting it in the queue would make it wait behind announcements for headroom it does not spend
/// and would not protect the budget it actually uses. The cadence is its rate limiter.
/// </para>
/// <para>
/// <b>The line never claims more than was read.</b> A host that could not be read says so rather than
/// showing the last good numbers, because a stale count with no timestamp on it is indistinguishable
/// from a current one.
/// </para>
/// </remarks>
public sealed class BotPresenceService : IBotPresence, IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly IKgsmStateCache _cache;
    private readonly IPlayerRoster _roster;
    private readonly DiscordOptions _options;
    private readonly ILogger<BotPresenceService> _logger;

    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;
    private string? _showing;

    /// <summary>
    /// What the presence says when this host could not be read at all. Deliberately not a count.
    /// </summary>
    internal const string Unreadable = "a host I can't read";

    public BotPresenceService(
        DiscordSocketClient discordClient,
        IKgsmStateCache cache,
        IPlayerRoster roster,
        IOptions<DiscordOptions> options,
        ILogger<BotPresenceService> logger)
    {
        _discordClient = discordClient;
        _cache = cache;
        _roster = roster;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (!_options.Presence)
        {
            _logger.LogInformation("Bot presence is switched off; the member list shows no activity.");
            return;
        }

        // The gateway's READY handler fires again on every reconnect, and a second loop would double
        // the rate against a limit measured per session.
        if (_loop is not null)
            return;

        _loop = Task.Run(() => RunAsync(_stopping.Token));
        _logger.LogInformation("Bot presence: refreshed every {Refresh}s.", _options.PresenceRefreshSeconds);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan period = TimeSpan.FromSeconds(_options.PresenceRefreshSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ApplyAsync(cancellationToken);
                await Task.Delay(period, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A failed refresh must not end the loop, and it must not shorten the period either:
                // whatever failed, the next attempt is one full cadence away.
                _logger.LogWarning(e, "The bot's presence could not be refreshed.");

                try
                {
                    await Task.Delay(period, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        string line = await ComposeAsync(cancellationToken);

        // Discord holds the presence for the session and Discord.Net re-sends it on a reconnect, so
        // re-stating the same line buys nothing and spends the budget the cadence exists to protect.
        if (line == _showing)
            return;

        await _discordClient.SetActivityAsync(new Game(line, ActivityType.Watching));
        _showing = line;

        _logger.LogDebug("Bot presence is now: Watching {Line}", line);
    }

    /// <summary>
    /// The line to show, read fresh every time.
    /// </summary>
    /// <remarks>
    /// The inventory is read separately from the roster for one reason: an empty roster means both "no
    /// servers" and "the host could not be read", and those are opposite things to put in front of
    /// somebody. The inventory is cached, so asking it costs nothing and settles which one it is.
    /// </remarks>
    internal async Task<string> ComposeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Instance> inventory;
        try
        {
            inventory = await _cache.GetInstancesAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The instance inventory could not be read for the bot's presence.");
            return Unreadable;
        }

        if (inventory.Count == 0)
            return "a host with no servers";

        IReadOnlyList<ServerRoster> rosters = await _roster.GetAllAsync(cancellationToken);

        // Servers this host has, and nothing came back about any of them. Reporting the count alone
        // would be true and useless; reporting "0 online" would be false.
        if (rosters.Count == 0)
            return Unreadable;

        return Describe(rosters);
    }

    /// <summary>
    /// The host in the handful of words a member list will show, with every uncertainty marked.
    /// </summary>
    /// <remarks>
    /// A count that could not be completed is written as a floor — <c>3+ online</c>, <c>12+ playing</c>
    /// — rather than as the partial total, which would read as the whole answer. Players are mentioned
    /// only when somebody is actually playing: "0 playing" is noise on a quiet host and a lie on a host
    /// whose games report nobody.
    /// </remarks>
    internal static string Describe(IReadOnlyList<ServerRoster> rosters)
    {
        int online = rosters.Count(r => r.Running == true);
        bool everyRunStateRead = rosters.All(r => r.Running is not null);

        var line = new StringBuilder()
            .Append(rosters.Count).Append(rosters.Count == 1 ? " server · " : " servers · ")
            .Append(online).Append(everyRunStateRead ? string.Empty : "+").Append(" online");

        int playing = rosters.Sum(r => r.Count ?? 0);
        if (playing > 0)
        {
            // A server that is not running needs no count to make the total complete — it has nobody
            // on it. Only a running server whose roster is unknown makes this a floor.
            bool everyCountKnown = rosters.All(r => r.Count is not null || r.Running != true);
            line.Append(" · ").Append(playing).Append(everyCountKnown ? string.Empty : "+").Append(" playing");
        }

        return line.ToString();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
