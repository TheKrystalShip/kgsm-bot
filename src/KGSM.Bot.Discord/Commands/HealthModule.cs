using Discord;
using Discord.Interactions;

using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Whether everything this bot depends on is answering, from inside Discord.
/// </summary>
/// <remarks>
/// <para>
/// The state this exists for is the one nothing else shows: the unit is active, the gateway is
/// connected, and the bot cannot do the thing somebody just asked it about. <c>/setup show</c>
/// answers a different question — what this guild is configured with — and the status socket answers
/// the Control Panel, which is no use to somebody who only has Discord.
/// </para>
/// <para>
/// <b>Always private, like <c>/logs</c>.</b> A failing check names host paths and the reasons stores
/// could not be opened, and somebody diagnosing does not need the channel's help.
/// </para>
/// <para>
/// Operator-gated: it is the inside of the machine rather than a question about a server.
/// </para>
/// </remarks>
[RequireTier(KgsmTier.Operator)]
public class HealthModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IBotHealth _health;
    private readonly ILogger<HealthModule> _logger;

    public HealthModule(IBotHealth health, ILogger<HealthModule> logger)
    {
        _health = health;
        _logger = logger;
    }

    [SlashCommand("health", "Whether everything this bot depends on is answering")]
    public async Task HealthAsync()
    {
        try
        {
            // Probing the engine spawns a kgsm process and the assistant probe is a network call —
            // both well outside the three seconds Discord allows an interaction to sit unanswered.
            await DeferAsync(ephemeral: true);

            _logger.LogInformation("Handling health command");

            IReadOnlyList<HealthCheck> checks = await _health.ReadAsync();

            await FollowupAsync(embed: Render(checks), ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling health command");
            await FollowupAsync($"An error occurred: {ex.Message}", ephemeral: true);
        }
    }

    /// <summary>One field per check, in the order they were run.</summary>
    private static Embed Render(IReadOnlyList<HealthCheck> checks)
    {
        var embed = new EmbedBuilder()
            .WithTitle("🩺 Bot health")
            .WithDescription(Summary(checks))
            .WithColor(Colour(checks))
            .WithCurrentTimestamp();

        foreach (HealthCheck check in checks)
            embed.AddField($"{Marker(check.Verdict)} {check.Name}", check.Detail);

        return embed.Build();
    }

    /// <summary>
    /// What the checks add up to, counted rather than judged.
    /// </summary>
    /// <remarks>
    /// A dependency this host was never given is left out of the count entirely — an assistant nobody
    /// deployed is not a fault, and putting it in the denominator would make a correctly configured
    /// host read as permanently short of something.
    /// </remarks>
    internal static string Summary(IReadOnlyList<HealthCheck> checks)
    {
        int failing = checks.Count(c => c.Verdict == HealthVerdict.Failing);
        int unknown = checks.Count(c => c.Verdict == HealthVerdict.Unknown);
        int off = checks.Count(c => c.Verdict == HealthVerdict.Off);
        int checkable = checks.Count - off;

        if (failing == 0 && unknown == 0)
        {
            return off == 0
                ? $"All {checkable} answering."
                : $"All {checkable} answering. {off} not configured on this host.";
        }

        var parts = new List<string>(2);
        if (failing > 0) parts.Add($"{failing} of {checkable} not answering");
        if (unknown > 0) parts.Add($"{unknown} couldn't be determined");

        return string.Join(", ", parts) + ".";
    }

    /// <summary>
    /// The colour follows the worst answer, and "could not tell" is deliberately not green — a check
    /// that did not reach an answer is the one somebody most needs to notice.
    /// </summary>
    private static Color Colour(IReadOnlyList<HealthCheck> checks) =>
        checks.Any(c => c.Verdict == HealthVerdict.Failing) ? Color.Red
        : checks.Any(c => c.Verdict == HealthVerdict.Unknown) ? Color.Orange
        : Color.Green;

    /// <summary>Four verdicts, four markers — a marker per answer, never one shared between two.</summary>
    internal static string Marker(HealthVerdict verdict) => verdict switch
    {
        HealthVerdict.Ok => "✅",
        HealthVerdict.Failing => "❌",
        HealthVerdict.Off => "➖",
        _ => "❔",
    };
}
