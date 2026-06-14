using System.Collections.Concurrent;
using System.Security.Cryptography;

using TheKrystalShip.Kgsm.Assistant;

namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// A small in-memory, single-use, TTL-bounded store for staged confirmations whose
/// payload is too long to encode in a Discord <c>customId</c> (a long SetConfig value,
/// e.g. <c>executable_arguments</c>). Most confirmations ride the id directly
/// (stateless — they survive a bot restart); only overflowing ones land here. An entry
/// is removed on first take, and expired entries are pruned lazily. Because it is
/// in-memory, a bot restart drops pending long edits — the user simply re-asks.
/// </summary>
public sealed class PendingEditStore
{
    private sealed record Entry(PendingConfirmation Confirmation, DateTimeOffset Expiry);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _now;

    public PendingEditStore() : this(TimeSpan.FromMinutes(10), () => DateTimeOffset.UtcNow) { }

    /// <summary>Test seam: inject the TTL and a clock.</summary>
    public PendingEditStore(TimeSpan ttl, Func<DateTimeOffset> now)
    {
        _ttl = ttl;
        _now = now;
    }

    /// <summary>Stashes a confirmation and returns its short, single-use id.</summary>
    public string Stash(PendingConfirmation confirmation)
    {
        PruneExpired();
        var id = NewId();
        _entries[id] = new Entry(confirmation, _now() + _ttl);
        return id;
    }

    /// <summary>
    /// Takes (and removes) the confirmation for <paramref name="id"/>. Returns false if it
    /// was never stored, was already taken, or has expired.
    /// </summary>
    public bool TryTake(string id, out PendingConfirmation confirmation)
    {
        confirmation = null!;
        if (string.IsNullOrEmpty(id) || !_entries.TryRemove(id, out var entry))
            return false;
        if (entry.Expiry < _now())
            return false; // taken but stale — treat as gone
        confirmation = entry.Confirmation;
        return true;
    }

    private void PruneExpired()
    {
        var cutoff = _now();
        foreach (var kv in _entries)
            if (kv.Value.Expiry < cutoff)
                _entries.TryRemove(kv.Key, out _);
    }

    // 12 uppercase-hex chars (48 bits): collision-safe for a short-lived store and
    // contains no '~', so it never clashes with the customId separator.
    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
