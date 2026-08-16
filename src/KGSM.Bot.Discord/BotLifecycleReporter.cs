using Discord;
using Discord.WebSocket;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Lifecycle;

namespace KGSM.Bot.Discord;

/// <summary>
/// Reports what this bot can and cannot currently do, to its own journal.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>systemd liveness is not health for this leaf, and the gateway's word is not either.</b> The
/// unit reports active, the gateway reports Connected, and the bot can still be unable to post a
/// single message: a guild that failed to populate, a channel it can no longer see, a store it could
/// not open. Every one of those is silence, and silence is indistinguishable from nothing having
/// happened.
/// </para>
/// <para>
/// The same facts <c>BotStatus</c> already computes, reported rather than only served. The status
/// socket answers whoever asks; nobody polls it on a schedule, so a bot that went silent at three in
/// the morning stayed silent until somebody opened a panel.
/// </para>
/// <para>
/// Every reading is taken from the live Discord client rather than from the store, because a guild
/// that is configured and not resolved is the interesting one and the store cannot tell them apart.
/// </para>
/// </remarks>
public sealed class BotLifecycleReporter(
    DiscordSocketClient client,
    IGuildStore guilds,
    IDiscordSendQueue queue,
    LeafLifecycle lifecycle,
    ILogger<BotLifecycleReporter> logger) : BackgroundService
{
    /// <summary>
    /// How often the bot's own state is re-read.
    /// </summary>
    /// <remarks>
    /// Slow on purpose. Every one of these is a condition that persists rather than a spike, the
    /// emitter reports only transitions, and a gateway reconnect that resolves inside half a minute is
    /// not something anybody needs a line about.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The gateway's own Ready is what makes this leaf able to do anything, and it arrives well
        // after the host has started — so readiness is hung off the event rather than reported here.
        client.Ready += OnReadyAsync;

        using var timer = new PeriodicTimer(Interval);

        try
        {
            // ⚠ The first reading is taken after one interval, not immediately. A gateway that has
            // not connected yet is a leaf still starting, not a degraded one — reporting at t=0 filed
            // a normal startup as a fault, with an unresolved guild beside it for the same reason. A
            // connect measured here takes under two seconds, so a gateway still down after thirty is
            // the real thing.
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Report();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            client.Ready -= OnReadyAsync;
        }
    }

    private Task OnReadyAsync()
    {
        lifecycle.MarkReady($"gateway ready as {client.CurrentUser?.Username ?? "unknown"}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes one reading and says what it sees.
    /// </summary>
    /// <remarks>
    /// Holds no state: it reports the condition on every tick and the emitter decides what changed. A
    /// steady state, healthy or not, produces nothing after the first line.
    /// </remarks>
    private void Report()
    {
        try
        {
            ReportGateway();
            ReportStore();
            ReportTopology();
            ReportQueue();
        }
        catch (Exception ex)
        {
            // Reporting must never take the surface down with it. A reading that threw is a reading
            // not taken, which the next tick retries.
            logger.LogWarning(ex, "could not read this bot's own state");
        }
    }

    private void ReportGateway()
    {
        if (client.ConnectionState == ConnectionState.Connected)
        {
            lifecycle.MarkRecovered(BotComponents.Gateway);
            return;
        }

        lifecycle.MarkDegraded(
            BotComponents.Gateway,
            $"the Discord gateway is {client.ConnectionState}; nothing is announced and no command "
            + "reaches this host while it stays that way");
    }

    private void ReportStore()
    {
        if (guilds.Available)
        {
            lifecycle.MarkRecovered(BotComponents.GuildStore);
            return;
        }

        lifecycle.MarkDegraded(
            BotComponents.GuildStore,
            $"the guild store could not be opened ({guilds.UnavailableReason ?? "no reason given"}); "
            + "nothing is announced anywhere and /setup refuses");
    }

    /// <summary>
    /// Whether every configured guild and channel is one the client can currently see.
    /// </summary>
    /// <remarks>
    /// ⚠ One component for all guilds and one for all channels, with the offenders named in the
    /// detail. A component per guild would grow the emitter's dedup set with every server this bot is
    /// invited to, and a guild removed while degraded would never recover.
    /// </remarks>
    private void ReportTopology()
    {
        List<string> lostGuilds = [];
        List<string> lostChannels = [];

        foreach (GuildTopology topology in guilds.Configured())
        {
            if (client.GetGuild(topology.GuildId) is null)
            {
                // Configured, connected, no guild — the state BotStatus exists to expose. Its channels
                // are not also counted: they are unreachable because the guild is, and reporting both
                // would name one fault twice.
                lostGuilds.Add(topology.GuildId.ToString());
                continue;
            }

            if (client.GetChannel(topology.AnnounceChannelId) is null)
                lostChannels.Add($"{topology.GuildId}/announce");

            foreach (GuildChannel binding in guilds.ChannelsIn(topology.GuildId))
            {
                if (client.GetChannel(binding.ChannelId) is null)
                    lostChannels.Add($"{topology.GuildId}/{binding.Instance}");
            }
        }

        if (lostGuilds.Count == 0)
            lifecycle.MarkRecovered(BotComponents.Guilds);
        else
            lifecycle.MarkDegraded(
                BotComponents.Guilds,
                $"configured and connected, but {lostGuilds.Count} guild(s) do not resolve "
                + $"({string.Join(", ", lostGuilds)}); everything meant for them is silently dropped");

        if (lostChannels.Count == 0)
            lifecycle.MarkRecovered(BotComponents.Channels);
        else
            lifecycle.MarkDegraded(
                BotComponents.Channels,
                $"{lostChannels.Count} bound channel(s) cannot be resolved "
                + $"({string.Join(", ", lostChannels)}); messages for them will never arrive");
    }

    private void ReportQueue()
    {
        SendQueueDepth depth = queue.Depth;

        if (!depth.BackingOff)
        {
            lifecycle.MarkRecovered(BotComponents.SendQueue);
            return;
        }

        lifecycle.MarkDegraded(
            BotComponents.SendQueue,
            $"the outbound queue is backing off after a rate limit or a Discord error "
            + $"({depth.Announcements} announcement(s), {depth.Background} background waiting); "
            + "messages are late rather than lost, and a gateway that reads healthy says nothing "
            + "about it");
    }
}
