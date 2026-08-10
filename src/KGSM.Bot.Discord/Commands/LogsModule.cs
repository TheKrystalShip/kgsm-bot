using System.Text;

using Discord.Interactions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Autocomplete;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// The tail of a server's log, as a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file, not a code block.</b> Discord truncates a long code block and wraps every line of it on
/// a phone, which is the exact opposite of what a log is for. An attachment opens in whatever the
/// reader already greps with, and carries the whole tail rather than the part that fitted.
/// </para>
/// <para>
/// <b>Ephemeral, and that is a privacy decision rather than a tidiness one.</b> A game server's log
/// routinely carries the network address of every player who connected. This bot already refuses to
/// put an address in a roster for that reason, and posting the raw log to the channel would publish
/// the same thing with more of it. The person who asked gets the file; the channel gets nothing.
/// </para>
/// <para>
/// Operator-gated: the log is the inside of the machine, and reading it is not the same permission as
/// asking whether a server is up.
/// </para>
/// </remarks>
[RequireTier(KgsmTier.Operator)]
public class LogsModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IServerInstanceService _instances;
    private readonly IKgsmStateCache _cache;
    private readonly ILogger<LogsModule> _logger;

    /// <summary>
    /// How much log is put in one attachment.
    /// </summary>
    /// <remarks>
    /// Under Discord's smallest upload allowance, which a guild with no boosts has and which a refused
    /// upload does not explain well. A tail long enough to hit this is truncated from the <i>front</i>:
    /// the newest lines are the ones somebody asked for.
    /// </remarks>
    private const int MaxAttachmentBytes = 7 * 1024 * 1024;

    public LogsModule(
        IServerInstanceService instances,
        IKgsmStateCache cache,
        ILogger<LogsModule> logger)
    {
        _instances = instances;
        _cache = cache;
        _logger = logger;
    }

    [SlashCommand("logs", "The tail of a server's log, as a file only you can see")]
    public async Task LogsAsync(
        [Summary(description: "Game server instance")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance,
        [Summary(description: "How many lines from the end. Default 200.")]
        [MinValue(1)] [MaxValue(2000)]
        int lines = 200)
    {
        try
        {
            // Reading a log spawns a kgsm process, which is well outside the three seconds Discord
            // allows an interaction to sit unanswered. Ephemeral from the defer, because that is where
            // it is decided — a followup cannot make a public interaction private afterwards.
            await DeferAsync(ephemeral: true);

            if (await _cache.GetInstanceAsync(instance) is null)
            {
                await FollowupAsync($"⚠️ There's no server called `{instance}` on this host.", ephemeral: true);
                return;
            }

            _logger.LogInformation("Handling logs command for instance {InstanceName} ({Lines} lines)",
                instance, lines);

            var result = await _instances.GetLogsAsync(instance, lines);
            if (result.IsFailure)
            {
                await FollowupAsync($"⚠️ I couldn't read the log for **{instance}**: {result.Error}",
                    ephemeral: true);
                return;
            }

            IReadOnlyList<string> log = result.Value!;
            if (log.Count == 0)
            {
                // Not a failure: a server that has not written anything yet, or one whose output the
                // engine keeps somewhere this host has nothing in.
                await FollowupAsync(
                    $"**{instance}** has no log output to show. It may not have been started yet.",
                    ephemeral: true);
                return;
            }

            (string text, bool truncated) = Fit(log);

            using var file = new MemoryStream(Encoding.UTF8.GetBytes(text));

            await FollowupWithFileAsync(
                fileStream: file,
                fileName: FileName(instance),
                text: Caption(instance, log.Count, lines, truncated),
                ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling logs command for instance {InstanceName}", instance);
            await FollowupAsync($"An error occurred: {ex.Message}", ephemeral: true);
        }
    }

    /// <summary>
    /// What the file is, said above it — including when it is not the whole of what was asked for.
    /// </summary>
    private static string Caption(string instance, int got, int asked, bool truncated)
    {
        var caption = new StringBuilder($"📄 **{instance}** — the last {got} line{(got == 1 ? "" : "s")}");

        // The engine keeps what it keeps. Asking for more than exists is not an error, and saying so
        // stops a short file reading as a truncated one.
        if (got < asked)
            caption.Append($" (all there is; you asked for {asked})");

        if (truncated)
            caption.Append(" — trimmed from the top to fit Discord's upload limit");

        return caption.ToString();
    }

    /// <summary>
    /// The log as one document, trimmed from the front if it is too large to upload.
    /// </summary>
    /// <remarks>
    /// Counted in bytes rather than characters, because the limit is in bytes and a log full of
    /// non-ASCII would otherwise measure short and be refused by Discord instead of trimmed here.
    /// </remarks>
    internal static (string Text, bool Truncated) Fit(IReadOnlyList<string> lines)
    {
        var kept = new List<string>(lines.Count);
        int bytes = 0;

        // Backwards: the end of a log is the part somebody asked for, so it is the part that survives.
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            int cost = Encoding.UTF8.GetByteCount(lines[i]) + 1;
            if (bytes + cost > MaxAttachmentBytes)
                return (string.Join('\n', Enumerable.Reverse(kept)), true);

            kept.Add(lines[i]);
            bytes += cost;
        }

        return (string.Join('\n', Enumerable.Reverse(kept)), false);
    }

    /// <summary>
    /// A filename that is safe wherever it lands, and says which server and when.
    /// </summary>
    private static string FileName(string instance)
    {
        var stem = new StringBuilder(instance.Length);
        foreach (char c in instance)
            stem.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');

        return $"{stem}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log";
    }
}
