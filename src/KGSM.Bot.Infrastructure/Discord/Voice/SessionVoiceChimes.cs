using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Writes a tone to the guild's voice connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session is resolved on use, not on construction.</b> The session is built with the
/// recogniser, the recogniser plays tones, and a constructor dependency in that direction is a cycle
/// the container refuses outright. Deferring the lookup costs a dictionary read per tone and keeps
/// the graph acyclic.
/// </para>
/// <para>
/// <b>It never throws and never reports.</b> Not being in a voice channel is the ordinary case for a
/// guild nobody invited the bot into, and a tone that cannot play is not a fault worth interrupting
/// anything over.
/// </para>
/// </remarks>
internal sealed class SessionVoiceChimes : IVoiceChimes
{
    /// <summary>
    /// How close together the same tone has to be to count as the same tone.
    /// </summary>
    /// <remarks>
    /// Two things can mark one moment: the trigger spotted while somebody was still saying it, and
    /// the same trigger read again when they stopped. Both are correct and the room only needs to
    /// hear it once — a tone repeated a second later is heard as a stutter rather than as a second
    /// piece of news. Long enough to cover the gap that ends a sentence, short enough that a genuine
    /// second request is still marked.
    /// </remarks>
    private static readonly TimeSpan Recently = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a tone will wait for the connection to stop talking before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <b>A tone is only true at the moment it plays.</b> "Your turn" arriving after the bot has
    /// been speaking for three seconds describes a moment that has already gone, and the person hears
    /// it against whatever is happening instead — which is worse than hearing nothing, because it
    /// invites them to talk into something that is not listening for them.
    /// </remarks>
    private static readonly TimeSpan WillWait = TimeSpan.FromMilliseconds(400);

    private readonly Func<IVoiceSessions> _sessions;
    private readonly VoiceOptions _options;
    private readonly ILogger<SessionVoiceChimes> _logger;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, VoiceChime), DateTimeOffset> _played = new();

    public SessionVoiceChimes(
        Func<IVoiceSessions> sessions,
        IOptions<DiscordOptions> options,
        ILogger<SessionVoiceChimes> logger)
    {
        _sessions = sessions;
        _options = options.Value.Voice;
        _logger = logger;
    }

    public async Task PlayAsync(ulong guildId, VoiceChime chime, CancellationToken ct = default)
    {
        if (!_options.Chimes) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset last = _played.AddOrUpdate(
            (guildId, chime), now, (_, before) => now - before < Recently ? before : now);

        if (last != now) return;

        try
        {
            Result played = await _sessions()
                .SpeakAsync(guildId, VoiceChimes.Pcm(chime), WillWait, ct);

            if (played.IsFailure)
                _logger.LogTrace("Voice: could not play the {Chime} tone: {Reason}", chime, played.Error);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: could not play the {Chime} tone", chime);
        }
    }
}
