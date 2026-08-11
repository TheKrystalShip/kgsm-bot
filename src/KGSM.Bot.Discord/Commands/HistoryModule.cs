using System.Text;

using Discord;
using Discord.Interactions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Autocomplete;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// What happened — one server's, or the whole host's, read back out of the engine's journal.
/// </summary>
/// <remarks>
/// <para>
/// The question this answers is "what happened overnight", and every way of failing to answer it is
/// rendered as itself. An unreadable journal is not a quiet host; a window reaching further back than
/// the journal keeps is answered from where the record starts and says so; a scan that hit its budget
/// says it is a prefix. The one empty answer that means "nothing happened" is the one with a readable
/// journal behind it.
/// </para>
/// <para>
/// <b>The engine's event vocabulary is bigger than the bot's.</b> A day on this host carries port
/// openings, UPnP forwards, deploy phases and prune results that no announcement kind exists for, and
/// a renderer that understood only the announced kinds would drop most of what it was asked about. So
/// the types worth naming are named, and everything else is rendered from the engine's own word —
/// which is also what a type added upstream tomorrow gets, with no change here.
/// </para>
/// </remarks>
[RequireTier(KgsmTier.Viewer)]
public class HistoryModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IServerHistory _history;
    private readonly IKgsmStateCache _cache;
    private readonly DiscordOptions _options;
    private readonly ILogger<HistoryModule> _logger;

    /// <summary>
    /// How many events are read for one question.
    /// </summary>
    /// <remarks>
    /// Well above a busy day on a host this size (measured: under a hundred), so the cap is a bound on
    /// the pathological case rather than a limit anybody meets. Reaching it is reported, because the
    /// newest two hundred of a longer list is a different answer from all of it.
    /// </remarks>
    private const int QueryLimit = 200;

    /// <summary>The most lines put in one embed, whatever the byte budget allows.</summary>
    private const int MaxLines = 40;

    /// <summary>Room for the lines, inside Discord's 4096-character description limit.</summary>
    private const int DescriptionBudget = 3800;

    public HistoryModule(
        IServerHistory history,
        IKgsmStateCache cache,
        IOptions<DiscordOptions> options,
        ILogger<HistoryModule> logger)
    {
        _history = history;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    private bool Quietly => _options.EphemeralReads;

    [SlashCommand("history", "What happened recently — one server's, or the whole host's")]
    public async Task HistoryAsync(
        [Summary(description: "Game server instance. Leave empty for the whole host.")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string? instance = null,
        [Summary(description: "How far back to look. Default 24 hours.")]
        [MinValue(1)] [MaxValue(720)]
        int hours = 24)
    {
        try
        {
            // Scanning the journal reads files off disk, which is not something to leave an
            // interaction sitting on for three seconds.
            await DeferAsync(ephemeral: Quietly);

            if (instance is not null && await _cache.GetInstanceAsync(instance) is null)
            {
                await FollowupAsync($"⚠️ There's no server called `{instance}` on this host.", ephemeral: Quietly);
                return;
            }

            _logger.LogInformation("Handling history command for {Scope} over {Hours}h",
                instance ?? "the whole host", hours);

            var window = TimeSpan.FromHours(hours);
            HostHistory history = await _history.ReadAsync(instance, window, QueryLimit);

            if (!history.JournalReadable)
            {
                await FollowupAsync(
                    "⚠️ I couldn't read this host's event journal, so I don't know what happened. " +
                    "That's a different thing from nothing having happened — ask an admin to check the bot's log.",
                    ephemeral: Quietly);
                return;
            }

            await FollowupAsync(embed: Render(history, instance, window), ephemeral: Quietly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling history command for {Scope}", instance ?? "the whole host");
            await FollowupAsync($"An error occurred: {ex.Message}", ephemeral: Quietly);
        }
    }

    /// <summary>The window's events, and everything that qualifies the list.</summary>
    private static Embed Render(HostHistory history, string? instance, TimeSpan window)
    {
        string scope = instance is null ? "This host" : $"**{instance}**";

        var embed = new EmbedBuilder()
            .WithTitle($"🕘 {(instance is null ? "History" : $"History — {instance}")}")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp();

        if (history.Moments.Count == 0)
        {
            // A readable journal that matched nothing. The only empty answer that is an answer.
            embed.WithDescription($"{scope} recorded nothing in the last {Window(window)}.");
            return embed.WithFooter(Coverage(history, window) ?? "Read from this host's event journal.").Build();
        }

        (string lines, int shown) = Fit(history.Moments);
        embed.WithDescription(lines);

        embed.WithFooter(string.Join(" · ", Notes(history, window, shown).Where(n => n is not null)));
        return embed.Build();
    }

    /// <summary>
    /// Everything that qualifies the list, each stated only when it applies.
    /// </summary>
    private static IEnumerable<string?> Notes(HostHistory history, TimeSpan window, int shown)
    {
        yield return history.Moments.Count == shown
            ? $"{shown} event{(shown == 1 ? "" : "s")} in the last {Window(window)}"
            : $"Newest {shown} of {history.Moments.Count} in the last {Window(window)}";

        // The read stopped at its own cap, so the count above is a floor rather than the total.
        if (history.Moments.Count >= QueryLimit)
            yield return "there may be more further back";

        // The scan gave up before the end of the window: a prefix presented as a whole answer is the
        // one failure this cannot be allowed to look like.
        if (history.Truncated)
            yield return "the scan stopped early, so this is a partial answer";

        yield return Coverage(history, window);
    }

    /// <summary>
    /// Said only when the window asked for more than the journal still keeps — otherwise the answer
    /// covers what was asked and there is nothing to qualify.
    /// </summary>
    private static string? Coverage(HostHistory history, TimeSpan window)
    {
        if (history.CoverageFrom is not DateTimeOffset from)
            return null;

        return from > DateTimeOffset.UtcNow.Subtract(window)
            ? $"the journal only goes back to {from:d MMM}"
            : null;
    }

    /// <summary>
    /// As many lines as fit, newest first, counted in characters against the embed's own limit.
    /// </summary>
    internal static (string Text, int Shown) Fit(IReadOnlyList<HistoryMoment> moments)
    {
        var text = new StringBuilder();
        int shown = 0;

        foreach (HistoryMoment moment in moments)
        {
            if (shown == MaxLines)
                break;

            string line = Line(moment);
            if (text.Length + line.Length + 1 > DescriptionBudget)
                break;

            if (shown > 0)
                text.Append('\n');
            text.Append(line);
            shown++;
        }

        return (text.ToString(), shown);
    }

    /// <summary>
    /// One event as one line.
    /// </summary>
    /// <remarks>
    /// The timestamp is Discord's own relative marker, so it reads correctly in whatever timezone the
    /// person is in without this having to guess at one. Instance, detail and actor are each dropped
    /// when the event did not carry them, rather than being filled with a placeholder.
    /// </remarks>
    internal static string Line(HistoryMoment moment)
    {
        var line = new StringBuilder($"<t:{moment.At.ToUnixTimeSeconds()}:R> ");

        if (moment.Instance is string name)
            line.Append("**").Append(name).Append("** ");

        (string phrase, bool completes) = Describe(moment.Type);
        line.Append(phrase);

        // A detail that repeats the server's own name says nothing twice — an instance installed from
        // the blueprint it is named after is the common case, and "stationeers was installed from
        // stationeers" is worse than the sentence without it.
        if (Detail(moment) is string detail)
            line.Append(completes ? " " : " — ").Append(detail);

        if (moment.Actor is string actor)
            line.Append(" · ").Append(actor);

        return line.ToString();
    }

    private static string? Detail(HistoryMoment moment) =>
        string.Equals(moment.Detail, moment.Instance, StringComparison.Ordinal) ? null : moment.Detail;

    /// <summary>
    /// What an engine event type is called here, and how its detail attaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The named ones are the ones somebody scanning a day wants to pick out. Everything else falls
    /// through to the engine's own word with its prefix stripped — <c>instance_deploy_finished</c>
    /// reads as "deploy finished" — which is a rendering of what the engine said rather than a guess
    /// at what it meant. That is what makes this list safe to leave incomplete: an event type nobody
    /// has named still appears, still names its server and its actor, and is never dropped for being
    /// unrecognised.
    /// </para>
    /// <para>
    /// The second half says whether the detail finishes the sentence or is an aside. "was joined by
    /// Void" is a sentence; "had a setting changed — backup_time" names a thing that happened and
    /// then says which. Getting it wrong reads as a typo on every line the event appears on, which on
    /// a busy day is most of them.
    /// </para>
    /// <para>
    /// The three that get a marker are the three somebody is scanning <i>for</i>.
    /// </para>
    /// </remarks>
    internal static (string Phrase, bool Completes) Describe(string type) => type switch
    {
        "instance_started" => ("started", false),
        "instance_ready" => ("was ready to play", false),
        "instance_stopped" => ("stopped", false),
        "instance_restarted" => ("restarted", false),
        "instance_crashed" => ("⚠️ crashed", false),
        "instance_failed" => ("⚠️ gave up restarting", false),

        "instance_created" => ("was created from", true),
        "instance_installed" => ("was installed from", true),
        "instance_uninstalled" => ("was uninstalled", false),
        "instance_update_available" => ("has an update available", false),
        "instance_version_updated" => ("was updated to", true),

        "instance_backup_created" => ("was backed up", false),
        "instance_backup_restored" => ("⚠️ was restored from a backup", false),
        "instance_backups_pruned" => ("had old backups pruned", false),

        "instance_player_joined" => ("was joined by", true),
        "instance_player_left" => ("was left by", true),
        "instance_player_kicked" => ("kicked", true),
        "instance_player_banned" => ("banned", true),
        "instance_player_unbanned" => ("unbanned", true),

        "instance_config_changed" => ("had a setting changed", false),
        "instance_input_sent" => ("was sent console input", false),

        "instance_ports_opened" => ("had its ports opened", false),
        "instance_ports_closed" => ("had its ports closed", false),
        "instance_upnp_opened" => ("had a UPnP forward opened", false),
        "instance_upnp_closed" => ("had a UPnP forward closed", false),
        "instance_upnp_reasserted" => ("had its UPnP forward re-asserted", false),

        "blueprint_created" => ("blueprint created", false),
        "blueprint_updated" => ("blueprint updated", false),

        // An unrecognised type is an aside by default: nothing is known about how its payload reads,
        // and a separator is the one join that cannot produce a broken sentence.
        _ => (Derived(type), false),
    };

    /// <summary>
    /// The engine's word, made readable: the subject prefix dropped (the line already names the
    /// server) and the separators spaced out. Nothing is inferred — an unrecognised type is shown,
    /// not guessed at.
    /// </summary>
    private static string Derived(string type)
    {
        string stem = type.StartsWith("instance_", StringComparison.Ordinal) ? type[9..] : type;
        return stem.Replace('_', ' ');
    }

    /// <summary>The window in the words somebody asked for it in.</summary>
    private static string Window(TimeSpan window) =>
        window.TotalHours >= 48
            ? $"{window.TotalDays:0} days"
            : $"{window.TotalHours:0} hour{(window.TotalHours == 1 ? "" : "s")}";
}
