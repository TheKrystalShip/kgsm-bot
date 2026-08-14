namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Whether the speech models are wanted right now.
/// </summary>
/// <remarks>
/// <para>
/// Voice sessions come and go, and the models cost more than everything else the bot does put
/// together. This is how the part that knows about channels tells the part that owns the models — and
/// it is deliberately two hints rather than a start and a stop: what actually happens on each is a
/// decision for the implementation and for this host's configuration, not for the voice connection.
/// </para>
/// <para>
/// Both must be safe to call at any time, in any order, and from any thread. A join and a leave a
/// second apart, or two joins in different guilds, are ordinary.
/// </para>
/// </remarks>
public interface ISpeechEngine
{
    /// <summary>Speech is about to be wanted. Load whatever it takes, without making the caller wait.</summary>
    void Wake();

    /// <summary>Nothing is listening any more. Everything loaded may be given back.</summary>
    void Idle();
}
