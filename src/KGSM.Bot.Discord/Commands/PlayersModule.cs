using System.Text;

using Discord;
using Discord.Interactions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Autocomplete;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Who is playing — one server, or the whole host.
/// </summary>
/// <remarks>
/// <para>
/// The answer people ask for out loud, and the one this bot could not give. It reads the supervisor's
/// live session map through <see cref="IPlayerRoster"/> rather than asking a game directly: presence
/// is detected from log output or an RCON poll depending on the game, and which of those applies is
/// the supervisor's business, not a chat surface's.
/// </para>
/// <para>
/// <b>Every way of not knowing is said out loud.</b> A game that does not report its players, a
/// supervisor that could not be reached, and a stopped server are three different sentences, and none
/// of them is "0 online" — a server nobody can see into may be full.
/// </para>
/// </remarks>
[RequireTier(KgsmTier.Viewer)]
public class PlayersModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IPlayerRoster _roster;
    private readonly ILogger<PlayersModule> _logger;

    public PlayersModule(IPlayerRoster roster, ILogger<PlayersModule> logger)
    {
        _roster = roster;
        _logger = logger;
    }

    [SlashCommand("players", "Who is playing — on one server, or across the whole host")]
    public async Task PlayersAsync(
        [Summary(description: "Game server instance. Leave empty for every server.")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string? instance = null)
    {
        try
        {
            // Reading the inventory, each server's run state and the supervisor's map takes longer
            // than the three seconds Discord allows an interaction to sit unanswered.
            await DeferAsync();

            if (string.IsNullOrWhiteSpace(instance))
            {
                IReadOnlyList<ServerRoster> all = await _roster.GetAllAsync();
                await FollowupAsync(embed: RenderHost(all));
                return;
            }

            ServerRoster? one = await _roster.GetAsync(instance);
            if (one is null)
            {
                await FollowupAsync($"⚠️ There's no server called `{instance}` on this host.");
                return;
            }

            await FollowupAsync(embed: RenderServer(one));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling players command for instance {InstanceName}", instance);
            await FollowupAsync($"An error occurred: {ex.Message}");
        }
    }

    // ── one server ────────────────────────────────────────────────────────────────────────────

    private static Embed RenderServer(ServerRoster roster)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"Players on {roster.Server}")
            .WithColor(roster.Knowledge == RosterKnowledge.Known ? Color.DarkTeal : Color.LightGrey)
            .WithCurrentTimestamp();

        if (roster.Knowledge != RosterKnowledge.Known)
        {
            embed.WithDescription(Explain(roster));
            return embed.Build();
        }

        if (roster.Players.Count == 0)
        {
            embed.WithDescription("Nobody is connected right now.");
            return embed.Build();
        }

        embed.WithDescription($"**{roster.Players.Count}** connected.");

        var body = new StringBuilder();
        foreach (RosterPlayer player in roster.Players)
            body.Append("• ").AppendLine(player.Label ?? "*(a player the game did not name)*");

        embed.AddField("Connected", Fit(body.ToString(), 1024));
        return embed.Build();
    }

    // ── the whole host ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every server, one line each, with the servers that have somebody on them first.
    /// </summary>
    /// <remarks>
    /// The total counts only what is actually known. A host where one game reports nothing cannot be
    /// summed to a number, so the summary says how many servers it could not speak for rather than
    /// quietly leaving them out of a total that then reads as complete.
    /// </remarks>
    private static Embed RenderHost(IReadOnlyList<ServerRoster> rosters)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Who's playing")
            .WithColor(Color.DarkTeal)
            .WithCurrentTimestamp();

        if (rosters.Count == 0)
        {
            embed.WithDescription("No servers are installed on this host.");
            return embed.Build();
        }

        int known = rosters.Sum(r => r.Count ?? 0);
        int unaccounted = rosters.Count(r => r.Knowledge is RosterKnowledge.NotObservable or RosterKnowledge.Unavailable);

        embed.WithDescription(unaccounted == 0
            ? $"**{known}** playing across {rosters.Count} server{(rosters.Count == 1 ? "" : "s")}."
            : $"**{known}** playing on the servers I can see into — {unaccounted} of {rosters.Count} " +
              "I can't, so the real total may be higher.");

        var body = new StringBuilder();
        foreach (ServerRoster roster in rosters.OrderByDescending(r => r.Count ?? -1).ThenBy(r => r.Server, StringComparer.OrdinalIgnoreCase))
            body.Append(Marker(roster)).Append(" **").Append(roster.Server).Append("** — ").AppendLine(Summarize(roster));

        embed.AddField("Servers", Fit(body.ToString(), 1024));
        return embed.Build();
    }

    private static string Marker(ServerRoster roster) => roster.Knowledge switch
    {
        RosterKnowledge.Known when roster.Players.Count > 0 => "🟢",
        RosterKnowledge.Known => "⚪",
        RosterKnowledge.Stopped => "🔴",
        _ => "❔",
    };

    /// <summary>
    /// One server's line. Names are listed while they fit on it, because "3 playing" is a worse
    /// answer than "3 playing — alice, bob, carol" to everyone who asked.
    /// </summary>
    private static string Summarize(ServerRoster roster)
    {
        if (roster.Knowledge != RosterKnowledge.Known)
            return Explain(roster);

        if (roster.Players.Count == 0)
            return "nobody connected";

        string[] names = [.. roster.Players.Select(p => p.Label).Where(l => l is not null).Cast<string>()];
        string count = $"{roster.Players.Count} playing";

        if (names.Length == 0)
            return count;

        string joined = string.Join(", ", names);
        return joined.Length <= 80 ? $"{count} — {joined}" : count;
    }

    /// <summary>
    /// Why there is no number, in the words that keep each reason distinct.
    /// </summary>
    /// <remarks>
    /// <see cref="RosterKnowledge.NotObservable"/> and <see cref="RosterKnowledge.Unavailable"/> both
    /// mean "I can't tell you", and they are still worth separating: the first is permanent and about
    /// the game, the second is a component being down and will fix itself. An operator reads them
    /// differently, and only one of them is worth reporting as a fault.
    /// </remarks>
    private static string Explain(ServerRoster roster)
    {
        // Ahead of the knowledge state, because it is the more specific answer and it names something
        // a person can act on. "I couldn't ask the supervisor" would be true here and useless.
        if (roster.LibraryAway)
        {
            return string.IsNullOrWhiteSpace(roster.Library)
                ? "its library is away, so nothing about it can be read"
                : $"library `{roster.Library}` is away, so nothing about it can be read";
        }

        return roster.Knowledge switch
        {
            RosterKnowledge.Stopped => "stopped, so nobody is on it",
            RosterKnowledge.NotObservable => "this game doesn't report its players, so I can't tell you who's on it",
            RosterKnowledge.Unavailable => "I couldn't ask the supervisor, so I don't know",
            _ => "unknown",
        };
    }

    /// <summary>
    /// Discord caps an embed field at 1024 characters. A host with more servers than fit gets as many
    /// as do plus a line saying so, rather than a silently short list.
    /// </summary>
    private static string Fit(string text, int limit)
    {
        if (text.Length <= limit)
            return text;

        const string notice = "…and more than fits in one message.";
        int room = limit - notice.Length - 1;
        int cut = text.LastIndexOf('\n', Math.Min(room, text.Length - 1));

        return (cut <= 0 ? text[..room] : text[..cut]) + "\n" + notice;
    }
}
