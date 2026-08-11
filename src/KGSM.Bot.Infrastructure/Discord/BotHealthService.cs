using global::Discord;
using global::Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// Every dependency this bot has, asked directly, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for is the one systemd cannot see: the unit is active, the gateway says
/// Connected, and the bot is unable to do the thing somebody is asking it about. An unreadable account
/// store refuses every command, a missing engine answers none of them, and neither shows up as
/// anything but a process that is running.
/// </para>
/// <para>
/// <b>Each check is its own answer and no check is inferred from another.</b> They fail
/// independently in practice — the engine and Discord have nothing to do with each other — so a
/// summary that took one as evidence for the next would report a state that was never measured.
/// </para>
/// <para>
/// A check that throws is reported as a failing check carrying the exception's own words. Nothing
/// here may propagate: the whole point is to be answerable while things are broken.
/// </para>
/// </remarks>
public sealed class BotHealthService(
    DiscordSocketClient client,
    IDiscordSendQueue queue,
    IServerInstanceService instances,
    IServerHistory history,
    IKgsmAccounts accounts,
    IGuildStore guilds,
    IAssistantTurnClient assistant,
    ILogger<BotHealthService> logger) : IBotHealth
{
    private readonly DiscordSocketClient _client = client;
    private readonly IDiscordSendQueue _queue = queue;
    private readonly IServerInstanceService _instances = instances;
    private readonly IServerHistory _history = history;
    private readonly IKgsmAccounts _accounts = accounts;
    private readonly IGuildStore _guilds = guilds;
    private readonly IAssistantTurnClient _assistant = assistant;
    private readonly ILogger<BotHealthService> _logger = logger;

    /// <summary>
    /// How far back the journal is asked about. Long enough that a host which simply had a quiet
    /// morning still has something in the window, so "nothing recent" is rare enough to be worth
    /// reading when it does appear.
    /// </summary>
    private static readonly TimeSpan JournalWindow = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealthCheck>> ReadAsync(CancellationToken ct = default)
    {
        // In triage order: whether it can speak, whether it is keeping up, whether it can act,
        // whether it can hear, whether it can authorize, whether it knows where to speak, and last
        // the optional half.
        return
        [
            Gateway(),
            Outbound(),
            await Engine(),
            await Journal(ct),
            Accounts(),
            Guilds(),
            await Assistant(ct),
        ];
    }

    /// <summary>
    /// The gateway's own word for what it is doing.
    /// </summary>
    /// <remarks>
    /// Connecting and Disconnecting are neither: a reconnect is normal and reporting one as a fault
    /// sends somebody looking for a problem that resolves itself in seconds. Latency is reported only
    /// once a heartbeat has completed — Discord.Net says 0 until then, and a 0ms link reads as
    /// impossibly good rather than as unmeasured.
    /// </remarks>
    private HealthCheck Gateway()
    {
        ConnectionState state = _client.ConnectionState;

        if (state != ConnectionState.Connected)
        {
            return new HealthCheck("Discord gateway",
                state == ConnectionState.Disconnected ? HealthVerdict.Failing : HealthVerdict.Unknown,
                $"{state}.");
        }

        string latency = _client.Latency > 0 ? $"{_client.Latency}ms round-trip" : "no heartbeat measured yet";
        return new HealthCheck("Discord gateway", HealthVerdict.Ok, $"Connected, {latency}.");
    }

    /// <summary>
    /// What is waiting to go out.
    /// </summary>
    /// <remarks>
    /// A backlog is not a fault — a burst drains — but a queue that is holding off after a rate limit
    /// is the state that takes the whole surface down if it persists, and it is invisible from
    /// everywhere else. Connected, configured, and messages arriving minutes late is a real condition
    /// whose only symptom is a depth that does not come back down.
    /// </remarks>
    private HealthCheck Outbound()
    {
        SendQueueDepth depth = _queue.Depth;
        string waiting = $"{depth.Announcements} announcement{(depth.Announcements == 1 ? "" : "s")} " +
                         $"and {depth.Background} housekeeping message{(depth.Background == 1 ? "" : "s")} waiting";

        return depth.BackingOff
            ? new HealthCheck("Outbound queue", HealthVerdict.Failing,
                $"Holding off after a rate limit or a Discord error — {waiting}.")
            : new HealthCheck("Outbound queue", HealthVerdict.Ok, $"Draining — {waiting}.");
    }

    /// <summary>
    /// Whether kgsm answers, asked by actually asking it.
    /// </summary>
    /// <remarks>
    /// The uncached list rather than the inventory cache: a cache serves its last good answer for as
    /// long as its TTL says to, which is the correct thing for it to do and the wrong thing to call a
    /// health check. This costs one kgsm process, which is why nothing runs it on a timer.
    /// </remarks>
    private async Task<HealthCheck> Engine()
    {
        try
        {
            Result<IReadOnlyDictionary<string, Instance>> all = await _instances.GetAllAsync();

            if (all.IsFailure)
                return new HealthCheck("KGSM engine", HealthVerdict.Failing, Sentence(all.Error));

            int count = all.Value!.Count;
            return new HealthCheck("KGSM engine", HealthVerdict.Ok,
                $"Answering — {count} server{(count == 1 ? "" : "s")} installed.");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Health check: the engine could not be asked for its instances.");
            return new HealthCheck("KGSM engine", HealthVerdict.Failing, Sentence(e.Message));
        }
    }

    /// <summary>
    /// Whether the engine's event journal can be read, and how recently anything was written to it.
    /// </summary>
    /// <remarks>
    /// <b>These are two facts and they are not combined.</b> Readable says the record is there; the
    /// newest entry's age says whether anything has been happening. A quiet host is not a broken one,
    /// so the age is reported and left for a person to judge — inferring a fault from silence would
    /// call every idle weekend an outage.
    /// </remarks>
    private async Task<HealthCheck> Journal(CancellationToken ct)
    {
        try
        {
            HostHistory recent = await _history.ReadAsync(null, JournalWindow, 1, ct);

            if (!recent.JournalReadable)
                return new HealthCheck("Event journal", HealthVerdict.Failing,
                    "Absent or unreadable — nothing this host does is being announced.");

            if (recent.Moments.Count == 0)
                return new HealthCheck("Event journal", HealthVerdict.Ok,
                    "Readable, and nothing has been written to it in the last 7 days.");

            return new HealthCheck("Event journal", HealthVerdict.Ok,
                $"Readable — the newest entry is {Age(DateTimeOffset.UtcNow - recent.Moments[0].At)} old.");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Health check: the event journal could not be read.");
            return new HealthCheck("Event journal", HealthVerdict.Failing, Sentence(e.Message));
        }
    }

    /// <summary>
    /// Whether this host's KGSM accounts can be read — which decides whether anybody may do anything.
    /// </summary>
    private HealthCheck Accounts() =>
        _accounts.Available
            ? new HealthCheck("KGSM accounts", HealthVerdict.Ok, "Readable — commands can be authorized.")
            : new HealthCheck("KGSM accounts", HealthVerdict.Failing,
                Sentence(_accounts.UnavailableReason ?? "The account store could not be opened") +
                " Every command that needs authorization refuses until it can be.");

    /// <summary>
    /// Whether the bot's own store can be read, and how many Discord servers are set up in it.
    /// </summary>
    /// <remarks>
    /// No guilds is a real state and deliberately not a fault: a bot invited everywhere and set up
    /// nowhere is silent by design, and calling that broken would flag every fresh install.
    /// </remarks>
    private HealthCheck Guilds()
    {
        if (!_guilds.Available)
            return new HealthCheck("Guild store", HealthVerdict.Failing,
                Sentence(_guilds.UnavailableReason ?? "The guild store could not be opened") +
                " Nothing is announced anywhere and /setup refuses.");

        int configured = _guilds.Configured().Count();
        return new HealthCheck("Guild store", HealthVerdict.Ok,
            configured == 0
                ? "Readable — no Discord server has run /setup, so nothing is announced anywhere yet."
                : $"Readable — {configured} Discord server{(configured == 1 ? "" : "s")} set up.");
    }

    /// <summary>
    /// The conversational half, which is optional and says so.
    /// </summary>
    /// <remarks>
    /// Unconfigured is <see cref="HealthVerdict.Off"/> rather than a failure: a host that never
    /// deployed the assistant leaf has not broken anything, and slash commands, announcements and the
    /// status board are unaffected either way.
    /// </remarks>
    private async Task<HealthCheck> Assistant(CancellationToken ct)
    {
        if (!_assistant.IsConfigured)
            return new HealthCheck("Assistant", HealthVerdict.Off,
                "No assistant is configured on this host, so @-mentioning the bot does nothing. " +
                "Slash commands and announcements are unaffected.");

        try
        {
            return await _assistant.IsAvailableAsync(ct)
                ? new HealthCheck("Assistant", HealthVerdict.Ok, "Answering.")
                : new HealthCheck("Assistant", HealthVerdict.Failing,
                    "Configured but not answering — @-mentioning the bot will say so rather than reply.");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Health check: the assistant could not be reached.");
            return new HealthCheck("Assistant", HealthVerdict.Failing, Sentence(e.Message));
        }
    }

    /// <summary>A reason as a sentence, whatever shape the thing that produced it left it in.</summary>
    private static string Sentence(string? reason)
    {
        string text = string.IsNullOrWhiteSpace(reason) ? "No reason was given" : reason.Trim();
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }

    /// <summary>
    /// An age in the largest unit that still says something useful.
    /// </summary>
    /// <remarks>
    /// The plural follows the number that is printed, not the one it was rounded from — 90 seconds
    /// prints as 2 and has to read as "2 minutes".
    /// </remarks>
    internal static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        return age.TotalMinutes < 1 ? "less than a minute"
            : age.TotalHours < 1 ? Unit(age.TotalMinutes, "minute")
            : age.TotalDays < 1 ? Unit(age.TotalHours, "hour")
            : Unit(age.TotalDays, "day");
    }

    private static string Unit(double value, string unit)
    {
        long whole = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        return $"{whole} {unit}{(whole == 1 ? "" : "s")}";
    }
}
