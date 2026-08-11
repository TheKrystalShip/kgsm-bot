using System.Collections.Concurrent;
using System.Security.Cryptography;

using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Discord;

/// <inheritdoc cref="IStagedRestores" />
public sealed class StagedRestores : IStagedRestores
{
    /// <summary>
    /// How long a proposal stands. Long enough to read what it says and check the date on the backup;
    /// short enough that a message scrolled past hours ago cannot still roll a server back.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, StagedRestore> _staged = new(StringComparer.Ordinal);
    private readonly ILogger<StagedRestores> _logger;

    public StagedRestores(ILogger<StagedRestores> logger) => _logger = logger;

    /// <inheritdoc />
    public string Stage(string instanceName, string backupId, ulong proposedToDiscordUserId)
    {
        // Cryptographically random rather than sequential: the handle is the only thing standing
        // between a customId somebody can type and a destructive action, and a guessable one is a
        // restore anybody in the channel can trigger against a server they never asked about.
        string handle = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        _staged[handle] = new StagedRestore(
            instanceName, backupId, proposedToDiscordUserId, DateTimeOffset.UtcNow);

        // Swept here rather than on a timer: proposals are rare, the collection is tiny, and a timer
        // would be a background loop existing only to delete a handful of records.
        Sweep();

        return handle;
    }

    /// <inheritdoc />
    public StagedRestore? Peek(string handle) =>
        _staged.TryGetValue(handle, out StagedRestore? staged)
        && DateTimeOffset.UtcNow - staged.ProposedAtUtc <= Lifetime
            ? staged
            : null;

    /// <inheritdoc />
    public StagedRestore? Redeem(string handle)
    {
        if (!_staged.TryRemove(handle, out StagedRestore? staged))
            return null;

        if (DateTimeOffset.UtcNow - staged.ProposedAtUtc > Lifetime)
        {
            _logger.LogInformation(
                "A restore of {InstanceName} was confirmed after it had expired; it was not run.",
                staged.InstanceName);
            return null;
        }

        return staged;
    }

    /// <inheritdoc />
    public void Cancel(string handle) => _staged.TryRemove(handle, out _);

    private void Sweep()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - Lifetime;

        foreach (KeyValuePair<string, StagedRestore> entry in _staged)
        {
            if (entry.Value.ProposedAtUtc < cutoff)
                _staged.TryRemove(entry.Key, out _);
        }
    }
}
