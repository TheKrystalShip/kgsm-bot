namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Restores that have been proposed and not yet confirmed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The operation is held here and the button carries a handle</b>, which is the same shape the
/// assistant's confirmations use. Discord echoes a component's <c>customId</c> back and nothing else,
/// so anything that must survive from proposal to click has to fit in 100 characters — a server name
/// and a backup id together do not reliably, and a truncated one names a different backup rather than
/// failing. The handle is fixed-length and means nothing outside this process.
/// </para>
/// <para>
/// <b>In memory, and that is correct.</b> A restore proposed before a restart should not be
/// confirmable after one: nobody has been looking at that message for the whole outage, and a
/// destructive action that survives the process is one somebody can click by accident days later.
/// A handle the bot no longer knows is refused with an explanation, not silently.
/// </para>
/// </remarks>
public interface IStagedRestores
{
    /// <summary>Hold a proposed restore and return the handle that redeems it.</summary>
    /// <param name="instanceName">The server the backup would be rolled onto.</param>
    /// <param name="backupId">The backup, as the engine's manifest names it.</param>
    /// <param name="proposedToDiscordUserId">Who asked. Only they can confirm it.</param>
    string Stage(string instanceName, string backupId, ulong proposedToDiscordUserId);

    /// <summary>
    /// Look at the staged restore for <paramref name="handle"/> without taking it, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// So that a click which turns out not to be allowed does not consume the proposal it was not
    /// entitled to answer — the person who asked has still not answered, and their button must still
    /// work.
    /// </remarks>
    StagedRestore? Peek(string handle);

    /// <summary>
    /// Take the staged restore for <paramref name="handle"/>, or <see langword="null"/> when there is
    /// none — unknown, expired, or already redeemed.
    /// </summary>
    /// <remarks>
    /// <b>Redeeming removes it.</b> A confirmation that can be clicked twice is a restore that can run
    /// twice, and the second one rolls back whatever the first one produced.
    /// </remarks>
    StagedRestore? Redeem(string handle);

    /// <summary>Drop a staged restore without running it.</summary>
    void Cancel(string handle);
}

/// <summary>One proposed restore, waiting for the person who asked to confirm it.</summary>
/// <param name="InstanceName">The server the backup would be rolled onto.</param>
/// <param name="BackupId">The backup, as the engine's manifest names it.</param>
/// <param name="ProposedToDiscordUserId">
/// Who asked. The click is authorized again at the tier, and it also has to be the same person — a
/// staged destructive action is not a button left lying around for the channel.
/// </param>
/// <param name="ProposedAtUtc">When it was staged, so an old proposal can be refused.</param>
public sealed record StagedRestore(
    string InstanceName,
    string BackupId,
    ulong ProposedToDiscordUserId,
    DateTimeOffset ProposedAtUtc);
