using KGSM.Bot.Core.Voice;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Plays the tones that mark whose turn it is to talk.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own seam rather than a call to the voice session</b>, because the recogniser is a
/// dependency of the session and cannot hold a reference back to it. What sits behind this resolves
/// the session at the moment of the first tone, by which time everything is built.
/// </para>
/// <para>
/// <b>A tone is best-effort and reports nothing.</b> Every caller is in the middle of doing something
/// a person is waiting on, and none of them has a sensible response to "the tone did not play" — the
/// answer it was marking is still coming, and the state it described is still true.
/// </para>
/// </remarks>
public interface IVoiceChimes
{
    /// <summary>Plays <paramref name="chime"/> in whatever channel the bot is in for this guild.</summary>
    Task PlayAsync(ulong guildId, VoiceChime chime, CancellationToken ct = default);
}
