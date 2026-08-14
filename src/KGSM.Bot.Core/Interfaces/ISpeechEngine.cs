namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Tells whatever holds the speech models that they are about to be wanted.
/// </summary>
/// <remarks>
/// <para>
/// Loading them takes seconds and the first sentence after that is slower again, so this is sent at
/// the moment the bot knows speech is coming — joining a voice channel — rather than at the moment
/// somebody speaks. It costs nothing on a host with no speech installed.
/// </para>
/// <para>
/// There is deliberately no counterpart. <b>How long the models stay loaded is not the bot's
/// decision</b>: they live in a leaf that serves every surface on the host, and a bot leaving a
/// channel is not evidence that nobody else is speaking. That leaf idles them out on its own
/// schedule, which is the only place the whole picture is visible.
/// </para>
/// <para>
/// Must be safe to call at any time, from any thread, and however many times. A join and a leave a
/// second apart, or two joins in different guilds, are ordinary.
/// </para>
/// </remarks>
public interface ISpeechEngine
{
    /// <summary>Speech is about to be wanted. Load whatever it takes, without making the caller wait.</summary>
    void Wake();
}
