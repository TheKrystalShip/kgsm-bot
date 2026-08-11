using System.Text.Json;

using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

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

    private static HistoryMoment Moment(EventHistoryEntry entry)
    {
        EventDescriptor descriptor = KgsmEventCatalog.Describe(entry.Type);
        return new HistoryMoment(
            entry.Ts, entry.Type, entry.Instance, entry.Actor,
            Detail(descriptor, entry.Data), descriptor.Weight);
    }

    /// <summary>
    /// The most specific field this surface may print off the payload, verbatim, or nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What each field is comes from <see cref="KgsmEventCatalog"/>; which of them says most about
    /// the moment is this surface's judgement.</b> The order below is that judgement — an event
    /// carrying several is described by the most specific it has — and the catalog is what decides
    /// whether a field may be printed here at all. So a payload field reclassified upstream changes
    /// what Discord shows without an edit here, and this holds no second opinion about which fields
    /// identify somebody.
    /// </para>
    /// <para>
    /// Nothing is computed: a value is printed as the engine wrote it or not at all, and an event
    /// carrying none of these shows no detail rather than a stand-in.
    /// </para>
    /// </remarks>
    private static readonly string[] Preference =
        ["Key", "PlayerName", "PlayerId", "Target", "NewVersion", "Blueprint", "Version", "ExitCode"];

    /// <summary>
    /// Whether this surface prints a field: only what the engine calls public, and only what is
    /// scalar enough to sit in a sentence.
    /// </summary>
    /// <remarks>
    /// <see cref="FieldSensitivity.Conditional"/> counts as personal, on the catalog's own
    /// instruction to a consumer that cannot resolve which it is — this one cannot, because only the
    /// game's blueprint says whether a moderation target is a name or an address, and printing an
    /// address is the thing the roster already refuses to do. <see cref="FieldShape.Ports"/> is
    /// structured and already has a renderer on <c>/connect</c> that a second one could disagree
    /// with; <see cref="FieldShape.Opaque"/> means nothing to a reader.
    /// </remarks>
    private static bool Printable(EventField field) =>
        field.Sensitivity == FieldSensitivity.Public
        && field.Shape is FieldShape.Text or FieldShape.Number
            or FieldShape.Version or FieldShape.Identity;

    private static string? Detail(EventDescriptor descriptor, JsonElement? data)
    {
        if (data is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (string name in Preference)
        {
            if (descriptor.Field(name) is not EventField field || !Printable(field))
                continue;

            if (!payload.TryGetProperty(name, out JsonElement value))
                continue;

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
