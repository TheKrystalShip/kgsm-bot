using System.Collections.Concurrent;

using Discord;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// Looks into a server the supervisor has given up on, and says what it found in the thread opened
/// for it.
/// </summary>
/// <remarks>
/// The thread under a give-up exists for the conversation about it, and it opens empty — at the one
/// moment when everything needed to explain what happened has already been gathered by the host and
/// nobody has read any of it. This asks the questions a person would ask first, before they arrive.
/// </remarks>
public interface IIncidentTriage
{
    /// <summary>
    /// Starts an investigation into <paramref name="announcement"/>, to be posted in
    /// <paramref name="thread"/>. Returns immediately.
    /// </summary>
    /// <remarks>
    /// Returning rather than awaiting is the contract, not a convenience. A turn takes as long as it
    /// takes, and the announcement path it is called from is the path every other guild's news is
    /// waiting on — an investigation held in front of that would delay the reporting of the next
    /// server to go down.
    /// </remarks>
    void Begin(ServerAnnouncement announcement, IThreadChannel thread, ulong guildId);
}

/// <inheritdoc />
public sealed class IncidentTriage : IIncidentTriage
{
    /// <summary>
    /// How long the same server is left alone in the same guild after one investigation. A give-up is
    /// already rare — the supervisor has to exhaust its retries to produce one — so this is a backstop
    /// against a server that gets restarted into the same wall repeatedly, not a routine gate.
    /// </summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    /// <summary>Discord hard-caps a single message at 2000 characters.</summary>
    private const int DiscordMessageLimit = 2000;

    /// <summary>
    /// The identity the investigation is asked under. Opaque and constant: it is not a person, and a
    /// name shaped like a Discord snowflake would suggest it was one.
    /// </summary>
    private const string TriageUserId = "system:triage";

    /// <summary>
    /// What the room shows as the speaker of the opening turn. It is read by people, and by the model
    /// on every later turn in that thread, so it says what the turn is rather than which service ran it.
    /// </summary>
    private const string TriageDisplayName = "Crash triage";

    // One investigation at a time, host-wide. A host that runs out of memory takes every server on it
    // down within seconds, and each give-up opens its own thread: without this they would arrive at
    // one Ollama together, and the last of them would report on a host that had moved on.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    // When each (guild, server) pair was last investigated.
    private readonly ConcurrentDictionary<(ulong Guild, string Instance), DateTimeOffset> _lastRun = new();

    private readonly IAssistantTurnClient _assistant;
    private readonly IDiscordSendQueue _queue;
    private readonly DiscordOptions _options;
    private readonly ILogger<IncidentTriage> _logger;

    public IncidentTriage(
        IAssistantTurnClient assistant,
        IDiscordSendQueue queue,
        IOptions<DiscordOptions> options,
        ILogger<IncidentTriage> logger)
    {
        _assistant = assistant;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    public void Begin(ServerAnnouncement announcement, IThreadChannel thread, ulong guildId)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(thread);

        if (!_options.IncidentTriage)
            return;

        if (!AnnouncementActions.OpensTriage(announcement.Kind))
            return;

        // No assistant on this host means the thread opens exactly as it did before this existed.
        // Said at debug rather than as a warning: a host with no assistant configured has not
        // misconfigured anything, and a line per crash saying so would be noise about a choice.
        if (!_assistant.IsConfigured)
        {
            _logger.LogDebug(
                "Not investigating {InstanceName}'s give-up: no assistant is configured on this host.",
                announcement.InstanceName);
            return;
        }

        var key = (guildId, announcement.InstanceName);
        var now = DateTimeOffset.UtcNow;
        if (_lastRun.TryGetValue(key, out var last) && now - last < Cooldown)
        {
            _logger.LogDebug(
                "Not investigating {InstanceName} in guild {GuildId}: it was investigated {Ago} ago.",
                announcement.InstanceName, guildId, now - last);
            return;
        }

        _lastRun[key] = now;

        _ = Task.Run(() => InvestigateAsync(announcement, thread, guildId));
    }

    private async Task InvestigateAsync(ServerAnnouncement announcement, IThreadChannel thread, ulong guildId)
    {
        try
        {
            await _oneAtATime.WaitAsync();
            try
            {
                Result<AssistantTurn> result = await _assistant.AskAsync(new AssistantAsk(
                    TriageUserId,
                    TriageDisplayName,
                    // Operator, because the console of the run that died is an authorized read and it
                    // is the one artifact that actually explains a crash — at viewer this reports on a
                    // server it cannot read the logs of. Nothing is thereby executed: the bot pins
                    // auto-run off for every caller, so anything the model proposes is staged, and a
                    // staged action is dropped below rather than offered to a thread that asked for none.
                    KgsmTier.Operator,
                    thread.Id.ToString(),
                    Prompt(announcement),
                    // The room, so this becomes the thread's opening turn rather than a wall of text
                    // beside it: whoever asks next continues THIS conversation, with the findings
                    // already in context.
                    Room: $"{guildId}-{thread.Id}"));

                if (result.IsFailure)
                {
                    // Said, not swallowed. Somebody is looking at a server that is down, and silence
                    // here is indistinguishable from an investigation that found nothing wrong.
                    _logger.LogWarning(
                        "Could not investigate {InstanceName}'s give-up: {Reason}",
                        announcement.InstanceName, result.Error);
                    await PostAsync(thread, announcement,
                        $"⚠️ I couldn't look into this: {result.Error}");
                    return;
                }

                AssistantTurn turn = result.Value!;

                if (turn.StagedActions.Count > 0)
                {
                    // Proposed to nobody. This turn was not asked for by a person, so there is no one
                    // whose click would mean anything — and the announcement above already carries the
                    // one action that belongs here, which is a restart.
                    _logger.LogDebug(
                        "Dropped {Count} staged action(s) from {InstanceName}'s investigation: nobody asked for them.",
                        turn.StagedActions.Count, announcement.InstanceName);
                }

                if (string.IsNullOrWhiteSpace(turn.Text))
                {
                    _logger.LogWarning(
                        "The investigation of {InstanceName}'s give-up came back empty.",
                        announcement.InstanceName);
                    return;
                }

                await PostAsync(thread, announcement, turn.Text);
                _logger.LogInformation(
                    "Investigated {InstanceName}'s give-up and posted the findings in its thread.",
                    announcement.InstanceName);
            }
            finally
            {
                _oneAtATime.Release();
            }
        }
        catch (Exception ex)
        {
            // This runs detached, so nothing above catches for it. An investigation that throws must
            // cost the investigation and nothing else — the announcement, its button and its thread
            // are already posted, and they are the parts that matter.
            _logger.LogError(ex,
                "Error investigating {InstanceName}'s give-up; the announcement and its thread are unaffected.",
                announcement.InstanceName);
        }
    }

    private Task PostAsync(IThreadChannel thread, ServerAnnouncement announcement, string text) =>
        _queue.SendAsync(
            $"post the investigation of {announcement.InstanceName} in thread {thread.Id}",
            SendLane.Announcement,
            () => thread.SendMessageAsync(Truncate(text), allowedMentions: AllowedMentions.None));

    /// <summary>
    /// What the assistant is asked. Composed here rather than left to the model to infer, so what an
    /// investigation looks at is a decision this repo makes and can change.
    /// </summary>
    /// <remarks>
    /// It states what is already known — the server, and whatever the event carried — and then asks
    /// for the things a person opening this thread would go and look at. The console of the run that
    /// died is named explicitly because it is the one that answers the question, and because a server
    /// the supervisor has given up on is not running: nothing has rotated its output away, so the
    /// crash is in the current run rather than a previous one.
    /// </remarks>
    private static string Prompt(ServerAnnouncement announcement)
    {
        var detail = announcement.Detail is { Length: > 0 } d ? $" ({d})" : string.Empty;

        return $"""
            The supervisor has given up restarting the server `{announcement.InstanceName}`{detail} and left it down.
            Nobody has asked you anything yet — you are opening the incident thread for this.

            Investigate and report what you find. Read its console output for the run that just died, run a
            health check on it, look at its recent events, and consult the operator guides for the game it
            runs if they say anything about this failure.

            Then write a short report for the people who will read this thread: what actually went wrong (quote
            the log line that shows it), whether it is likely to recur, and what you would do about it. If the
            evidence does not say why it died, say exactly that and say what you did check — do not guess at a
            cause. Do not offer to take any action; somebody here will decide that.

            Keep it under 1500 characters. Plain prose with a short bullet list where it helps.
            """;
    }

    private static string Truncate(string text) =>
        text.Length <= DiscordMessageLimit ? text : text[..(DiscordMessageLimit - 1)] + "…";
}
