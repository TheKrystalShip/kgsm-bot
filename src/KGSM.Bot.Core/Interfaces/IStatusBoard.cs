namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Keeps one message per guild current: every server, its run state, and how to reach it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An edit, never a rename.</b> Discord rate-limits channel edits hard enough that a channel name
/// cannot be kept in step with a server's state; a message edit is a different and far more generous
/// bucket, which is why the live picture lives in a message and the channel keeps whatever name a
/// human gave it.
/// </para>
/// <para>
/// <b>Marking dirty is not publishing.</b> A host reboot is one event per server arriving in the same
/// second, and fifteen edits for one fact is exactly how a bot gets throttled off the API — taking
/// the announcements with it. Callers say the picture changed; the board decides when to spend an
/// edit on it.
/// </para>
/// </remarks>
public interface IStatusBoard
{
    /// <summary>
    /// Start keeping the message current. Called once the gateway is ready, because there is nothing
    /// to edit before then.
    /// </summary>
    void Start();

    /// <summary>
    /// The picture changed. Cheap, non-blocking, and safe to call once per event in a burst — it sets
    /// a flag, and the next publishing window spends one edit on however many calls arrived.
    /// </summary>
    void Invalidate();

    /// <summary>
    /// Publish now, ignoring the coalescing window. For the moment a guild turns the message on, when
    /// somebody is waiting to see it appear.
    /// </summary>
    Task PublishAsync(ulong guildId, CancellationToken cancellationToken = default);
}
