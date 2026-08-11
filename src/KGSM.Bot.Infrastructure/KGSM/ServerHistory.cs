using System.Text.Json;

using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// What this host did, read back off the engine's journal through kgsm-lib.
/// </summary>
/// <remarks>
/// A thin adapter on purpose. The journal reader already answers a bounded window without an index
/// and reports its own limits — how far back it can still answer for, and whether the scan was cut
/// short — so the work here is carrying those signals across unflattened and lifting one readable
/// field off each payload.
/// </remarks>
public sealed class ServerHistory(
    IEventJournalHistory journal,
    ILogger<ServerHistory> logger) : IServerHistory
{
    private readonly IEventJournalHistory _journal = journal;
    private readonly ILogger<ServerHistory> _logger = logger;

    /// <inheritdoc />
    public async Task<HostHistory> ReadAsync(
        string? instance, TimeSpan window, int limit, CancellationToken ct = default)
    {
        var query = new EventHistoryQuery
        {
            Instance = string.IsNullOrWhiteSpace(instance) ? null : instance,
            SinceMs = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeMilliseconds(),
            Limit = limit,
        };

        EventHistoryPage page;
        try
        {
            page = await _journal.QueryAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The reader contracts not to throw for an absent or unreadable journal, so anything
            // arriving here is unexpected — reported as "could not read" rather than as a quiet host.
            _logger.LogError(e, "Reading the event journal failed for {Scope}.", instance ?? "the whole host");
            return HostHistory.Unreadable;
        }

        return new HostHistory(
            Moments: [.. page.Events.Select(Moment)],
            JournalReadable: page.JournalReadable,
            CoverageFrom: page.CoverageFrom,
            Truncated: page.Truncated);
    }

    private static HistoryMoment Moment(EventHistoryEntry entry) =>
        new(entry.Ts, entry.Type, entry.Instance, entry.Actor, Detail(entry.Data));

    /// <summary>
    /// One field off the payload, verbatim, or nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the judgement: an event carrying several of these is described by the most
    /// specific one it has, and an event carrying none of them shows no detail rather than a
    /// stand-in. Nothing here is computed — a value is printed as the engine wrote it or not at all,
    /// so a new event type gains a detail by carrying a field this already knows and loses nothing
    /// by carrying none.
    /// </para>
    /// <para>
    /// <b>Three fields are deliberately not read.</b> <c>PlayerAddr</c> is a network address, which
    /// identifies a connection rather than a person and is the same thing the roster refuses to
    /// print. <c>Command</c> is console input verbatim, and this surface answers a viewer.
    /// <c>Ports</c> already has one renderer, on <c>/connect</c>, and a second could disagree with it
    /// about the same server.
    /// </para>
    /// </remarks>
    private static readonly string[] DetailFields =
        ["Key", "PlayerName", "PlayerId", "Target", "NewVersion", "Blueprint", "Version", "ExitCode"];

    private static string? Detail(JsonElement? data)
    {
        if (data is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (string field in DetailFields)
        {
            if (!payload.TryGetProperty(field, out JsonElement value))
                continue;

            // Scalars only. A structured field rendered by a generic reader is JSON in a sentence,
            // and every structured payload here already has a renderer that knows what it means.
            string? text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return null;
    }
}
