namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// What this host did, read back out of the engine's event journal.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to the announcement reader, and deliberately a separate path. That one tails the
/// journal and stores no position, because an announcement is only worth making while it is current;
/// this one answers a question somebody just asked, against the durable record, and a window reaching
/// back over a restart is exactly what it is for.
/// </para>
/// <para>
/// Every way of not knowing is on the answer rather than thrown, because each of them renders
/// differently and none of them is "nothing happened".
/// </para>
/// </remarks>
public interface IServerHistory
{
    /// <summary>
    /// Reads what happened in a window, newest first.
    /// </summary>
    /// <param name="instance">One server's events, or null for the whole host.</param>
    /// <param name="window">How far back to look.</param>
    /// <param name="limit">The most events to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<HostHistory> ReadAsync(
        string? instance, TimeSpan window, int limit, CancellationToken ct = default);
}

/// <summary>
/// One window of history, and the three things that qualify it.
/// </summary>
/// <param name="Moments">What happened, newest first. Empty with a readable journal is a real answer.</param>
/// <param name="JournalReadable">
/// False when the journal is absent or could not be read. <b>The distinction this record exists to
/// carry</b>: an empty list means "nothing happened" only when this is true, and rendering the two
/// the same tells somebody their host was quiet on the strength of a permission error.
/// </param>
/// <param name="CoverageFrom">
/// The oldest moment the journal can still answer for. A window reaching earlier than this is
/// answered only from here, and saying so is what stops a partial history reading as a complete one.
/// Null when the journal holds nothing at all.
/// </param>
/// <param name="Truncated">
/// The scan stopped at its budget before reaching the end of the window, so this is a prefix of the
/// answer rather than the whole of it.
/// </param>
public sealed record HostHistory(
    IReadOnlyList<HistoryMoment> Moments,
    bool JournalReadable,
    DateTimeOffset? CoverageFrom,
    bool Truncated)
{
    /// <summary>A journal that could not be read at all.</summary>
    public static readonly HostHistory Unreadable = new([], false, null, false);
}

/// <summary>
/// One thing that happened, as the engine recorded it.
/// </summary>
/// <param name="At">When the engine emitted it.</param>
/// <param name="Type">
/// The raw engine event type, e.g. <c>instance_started</c>. Kept raw on purpose: the engine emits
/// far more kinds than this bot announces, and a reader that only understood the announced ones
/// would drop most of a real day.
/// </param>
/// <param name="Instance">The server it is about, or null for anything host-scoped.</param>
/// <param name="Actor">Who caused it, or null when the emitter supplied none. Never fabricated.</param>
/// <param name="Detail">
/// One field lifted verbatim off the payload — which setting changed, which player, which version.
/// Null when the payload carried none of them.
/// </param>
public sealed record HistoryMoment(
    DateTimeOffset At,
    string Type,
    string? Instance,
    string? Actor,
    string? Detail);
