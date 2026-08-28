using System.Text;

using Discord;
using Discord.Interactions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Autocomplete;
using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// The backups a server has, taking one, and rolling one back.
/// </summary>
/// <remarks>
/// <para>
/// <b>How consistent a capture was is measured, not assumed, and it is the thing worth reading.</b>
/// The engine records it per backup: <c>cold</c> (the server was stopped, so nothing could write
/// mid-archive), <c>flushed</c> (running, but it wrote its world out first), <c>hot</c> (running with
/// no usable save command — <b>the archive may be torn</b>), or nothing at all when the run state
/// could not be determined. A surface that flattened those into "backed up" would be hiding the
/// only part that decides whether the backup is worth having.
/// </para>
/// <para>
/// <b>Age is computed here, from the engine's timestamp.</b> Nothing upstream stores an age, which is
/// what lets the whole-host summary be cached without the number going wrong.
/// </para>
/// </remarks>
[RequireTier(KgsmTier.Viewer)]
public class BackupsModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IBackupInsight _backups;
    private readonly IServerInstanceService _instances;
    private readonly IKgsmStateCache _cache;
    private readonly IStagedRestores _staged;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<BackupsModule> _logger;

    /// <summary>How many are listed. A page of a Discord embed, not a database dump.</summary>
    private const int Listed = 10;

    public BackupsModule(
        IBackupInsight backups,
        IServerInstanceService instances,
        IKgsmStateCache cache,
        IStagedRestores staged,
        IInvocationContext invocation,
        ILogger<BackupsModule> logger)
    {
        _backups = backups;
        _instances = instances;
        _cache = cache;
        _staged = staged;
        _invocation = invocation;
        _logger = logger;
    }

    // ── reading ───────────────────────────────────────────────────────────────────────────────

    [SlashCommand("backups", "What backups a game server has, and how good each one is")]
    public async Task BackupsAsync(
        [Summary(description: "Game server instance")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        try
        {
            await DeferAsync();

            if (await _cache.GetInstanceAsync(instance) is null)
            {
                await FollowupAsync($"⚠️ There's no server called `{instance}` on this host.");
                return;
            }

            Result<IReadOnlyList<InstanceBackup>> result = await _backups.ListAsync(instance);
            if (result.IsFailure)
            {
                await FollowupAsync($"⚠️ I couldn't read **{instance}**'s backups: {result.Error}");
                return;
            }

            await FollowupAsync(embed: Render(instance, result.Value!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling backups command for instance {InstanceName}", instance);
            await FollowupAsync($"An error occurred: {ex.Message}");
        }
    }

    private static Embed Render(string instance, IReadOnlyList<InstanceBackup> backups)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"Backups of {instance}")
            .WithCurrentTimestamp();

        if (backups.Count == 0)
        {
            // A measured zero: the engine was asked and holds none. Worth saying plainly, because it
            // is the answer somebody needs before they find out the hard way.
            return embed
                .WithColor(Color.Orange)
                .WithDescription(
                    $"**{instance}** has never been backed up. `/backup {instance}` takes one now.")
                .Build();
        }

        InstanceBackup newest = backups[0];
        embed.WithColor(Color.DarkTeal)
             .WithDescription(
                 $"**{backups.Count}** backup{(backups.Count == 1 ? "" : "s")}, newest {Age(newest.CreatedAt)}.");

        var body = new StringBuilder();
        foreach (InstanceBackup backup in backups.Take(Listed))
        {
            body.Append(Marker(backup.Consistency)).Append(" `").Append(backup.Id).Append("`\n")
                .Append("    ").Append(Age(backup.CreatedAt))
                .Append(" · ").Append(Size(backup.SizeBytes))
                .Append(" · ").Append(Consistency(backup.Consistency));

            if (!string.IsNullOrWhiteSpace(backup.Version))
                body.Append(" · v").Append(backup.Version);

            body.AppendLine();
        }

        embed.AddField("Newest first", Fit(body.ToString(), 1024));

        if (backups.Count > Listed)
            embed.WithFooter($"Showing {Listed} of {backups.Count}.");

        return embed.Build();
    }

    // ── taking one ────────────────────────────────────────────────────────────────────────────

    [SlashCommand("backup", "Back up a game server now")]
    [Mutating]
    public async Task BackupAsync(
        [Summary(description: "Game server instance")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));

        try
        {
            if (await _cache.GetInstanceAsync(instance) is null)
            {
                await RespondAsync($"⚠️ There's no server called `{instance}` on this host.", ephemeral: true);
                return;
            }

            // Answered before the work starts, and left standing afterwards: archiving a game can run
            // for minutes, and Discord's three seconds are long gone by then. The reply says what is
            // happening rather than leaving somebody watching a spinner they cannot interpret.
            await RespondAsync(
                $"💾 Backing up **{instance}** — this can take a while on a large server. " +
                "The server keeps running; I'll say here when it's done.");

            _logger.LogInformation("Backing up {InstanceName}, asked for by {User}",
                instance, Context.User.Username);

            Result result = await _instances.CreateBackupAsync(instance);

            // The interaction token dies after fifteen minutes, and a big archive can outlast it. The
            // failure to say so is not a failure to back up, so it is logged rather than surfaced —
            // and the engine's own `backup created` announcement still lands in the channel.
            try
            {
                await FollowupAsync(result.IsSuccess
                    ? $"✅ **{instance}** is backed up. `/backups {instance}` shows it."
                    : $"⚠️ Backing up **{instance}** failed: {result.Error}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "The backup of {InstanceName} finished, but the reply could not be posted — the " +
                    "interaction had expired.", instance);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling backup command for instance {InstanceName}", instance);
            await FollowupAsync($"An error occurred: {ex.Message}");
        }
    }

    // ── rolling one back ──────────────────────────────────────────────────────────────────────

    [SlashCommand("restore", "Roll a game server back to a backup — replaces what is there now")]
    [Mutating]
    public async Task RestoreAsync(
        [Summary(description: "Game server instance")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance,
        [Summary(description: "Which backup. Leave empty for the newest.")]
        string? backup = null)
    {
        try
        {
            await DeferAsync();

            Result<IReadOnlyList<InstanceBackup>> all = await _backups.ListAsync(instance);
            if (all.IsFailure)
            {
                await FollowupAsync($"⚠️ I couldn't read **{instance}**'s backups: {all.Error}");
                return;
            }

            if (all.Value!.Count == 0)
            {
                await FollowupAsync($"**{instance}** has no backups, so there's nothing to roll back to.");
                return;
            }

            InstanceBackup? chosen = string.IsNullOrWhiteSpace(backup)
                ? all.Value[0]
                : all.Value.FirstOrDefault(b => string.Equals(b.Id, backup, StringComparison.OrdinalIgnoreCase));

            if (chosen is null)
            {
                await FollowupAsync(
                    $"⚠️ **{instance}** has no backup called `{backup}`. `/backups {instance}` lists them.");
                return;
            }

            // Staged rather than run. The handle is what the button carries, because a server name and
            // a backup id together do not reliably fit a Discord customId — and a truncated one names
            // a different archive rather than failing.
            string handle = _staged.Stage(instance, chosen.Id, Context.User.Id);

            if (!RestoreActionIds.Fits(handle))
            {
                await FollowupAsync("⚠️ I couldn't offer a confirmation button for that. Nothing was changed.");
                return;
            }

            var buttons = new ComponentBuilder()
                .WithButton("Restore", RestoreActionIds.Confirm(handle), ButtonStyle.Danger)
                .WithButton("Cancel", RestoreActionIds.Cancel(handle), ButtonStyle.Secondary)
                .Build();

            await FollowupAsync(embed: Proposal(instance, chosen), components: buttons);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling restore command for instance {InstanceName}", instance);
            await FollowupAsync($"An error occurred: {ex.Message}");
        }
    }

    /// <summary>
    /// What is about to happen, in the words somebody needs to decide against it.
    /// </summary>
    /// <remarks>
    /// The date and the consistency are the two facts a person checks before agreeing, so they are the
    /// two the proposal leads with — a confirmation that only says "are you sure?" is one nobody can
    /// answer correctly.
    /// </remarks>
    private static Embed Proposal(string instance, InstanceBackup backup)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"Roll {instance} back?")
            .WithColor(Color.Red)
            .WithDescription(
                $"This replaces **{instance}**'s current data with the backup below. " +
                "**What is there now is gone** unless it has a backup of its own.")
            .AddField("Backup", $"`{backup.Id}`")
            .AddField("Taken", Age(backup.CreatedAt), inline: true)
            .AddField("Size", Size(backup.SizeBytes), inline: true)
            .AddField("Capture", Consistency(backup.Consistency), inline: true)
            .WithFooter($"Expires in {StagedRestores.Lifetime.TotalMinutes:0} minutes.")
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(backup.Version))
            embed.AddField("Game version", backup.Version, inline: true);

        return embed.Build();
    }

    // ── shared rendering ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine's measured consistency, in words. An unrecognised value is printed as it came
    /// rather than mapped onto one of these — the engine owns this vocabulary, and a surface guessing
    /// what a new word means is how a torn archive gets described as a good one.
    /// </summary>
    internal static string Consistency(string? consistency) => consistency switch
    {
        "cold" => "cold — the server was stopped, so nothing could change mid-archive",
        "flushed" => "flushed — running, but it wrote its world out first",
        "hot" => "⚠️ hot — running with no way to make it save; this archive may be torn",
        null or "" => "unknown — the run state couldn't be read when this was taken",
        _ => consistency,
    };

    internal static string Marker(string? consistency) => consistency switch
    {
        "cold" or "flushed" => "🟢",
        "hot" => "⚠️",
        _ => "❔",
    };

    /// <summary>
    /// How long ago, or that nobody can say. A backup whose manifest carries no timestamp is real and
    /// restorable, and giving it an invented date would be worse than admitting the gap.
    /// </summary>
    internal static string Age(DateTimeOffset? taken)
    {
        if (taken is not DateTimeOffset when)
            return "at an unrecorded time";

        TimeSpan ago = DateTimeOffset.UtcNow - when;

        return ago switch
        {
            { TotalSeconds: < 0 } => $"dated {when:yyyy-MM-dd HH:mm} UTC",
            { TotalMinutes: < 2 } => "just now",
            { TotalHours: < 1 } => $"{ago.TotalMinutes:0} minutes ago",
            { TotalDays: < 1 } => $"{ago.TotalHours:0} hours ago",
            { TotalDays: < 31 } => $"{ago.TotalDays:0} days ago",
            _ => $"{when:yyyy-MM-dd}",
        };
    }

    internal static string Size(long bytes) => bytes switch
    {
        <= 0 => "size unrecorded",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
    };

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
