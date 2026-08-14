using System.Collections.Concurrent;

namespace KGSM.Bot.Core.Voice;

/// <summary>
/// The people the bot is currently waiting to hear back from, and until when.
/// </summary>
/// <remarks>
/// <para>
/// <b>A question the bot just asked does not need to be re-addressed to answer it.</b> Measured in a
/// real session: the assistant asked something, and answering it took "hey assistant, yes" — the bot
/// had spoken to that person a second earlier and still made them introduce themselves to reply. The
/// trigger exists to tell the bot's conversation apart from the room's, and immediately after it has
/// asked somebody a direct question there is nothing to tell apart.
/// </para>
/// <para>
/// <b>Per speaker and per channel, and spent by one utterance.</b> Everybody else in the room is
/// unaffected — the window belongs to the person who was asked — and it is consumed whether or not
/// what they said turns out to be an answer, so it can never accumulate into a microphone that is
/// simply open. A reply that asks a further question opens the next one.
/// </para>
/// <para>
/// <b>It changes who has to say the trigger, never what anybody may do.</b> An utterance let through
/// here is put to the assistant exactly as a triggered one is: same identity, same tier re-derived at
/// the turn, same propose-then-confirm rule. Nothing here can approve anything.
/// </para>
/// </remarks>
public sealed class VoiceAttention
{
    private readonly ConcurrentDictionary<(ulong Speaker, ulong Channel), VoiceWaiting> _open = new();

    /// <summary>Waits for this speaker's next utterance in this channel.</summary>
    public void Expect(ulong speakerId, ulong channelId, VoiceWaiting waiting) =>
        _open[(speakerId, channelId)] = waiting;

    /// <summary>
    /// What this speaker was being waited on for, consuming the window. Null when nobody was waiting
    /// on them or the wait had run out.
    /// </summary>
    /// <remarks>
    /// An expired window is removed on the way past rather than by a sweep: the most that can
    /// accumulate is one per person the bot has asked something, and the read that would notice it is
    /// the one doing the removing.
    /// </remarks>
    public VoiceWaiting? Take(ulong speakerId, ulong channelId, DateTimeOffset now) =>
        _open.TryRemove((speakerId, channelId), out VoiceWaiting? waiting) && waiting.Until > now
            ? waiting
            : null;

    /// <summary>Stops waiting on somebody, without their having said anything.</summary>
    public void Forget(ulong speakerId, ulong channelId) => _open.TryRemove((speakerId, channelId), out _);

    /// <summary>
    /// Whether a reply asked its reader something, and so expects them to say something back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question mark is the whole test, read past the markup a chat reply ends with — an answer
    /// that trails off in <c>**bold**</c> or a quote still asked a question. Deliberately nothing
    /// cleverer: a model's phrasing is not a signal to build a gate on, and the cost of reading this
    /// wrong is one window that goes unused or one utterance answered that was not meant for the bot.
    /// </para>
    /// <para>
    /// Only the last sentence counts. A reply that asks something in the middle and then answers it is
    /// not waiting on anybody.
    /// </para>
    /// </remarks>
    public static bool InvitesAnAnswer(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;

        ReadOnlySpan<char> text = reply.AsSpan().TrimEnd();
        while (text.Length > 0 && IsTrailingMarkup(text[^1])) text = text[..^1].TrimEnd();

        return text.Length > 0 && text[^1] == '?';
    }

    /// <summary>What a chat reply can end with that is decoration rather than punctuation.</summary>
    private static bool IsTrailingMarkup(char c) =>
        c is '*' or '_' or '`' or '~' or '"' or '\'' or '”' or '’' or ')' or ']' or '>';
}

/// <summary>Why the bot is listening to somebody without the trigger.</summary>
public enum VoiceWaitingFor
{
    /// <summary>It asked them a question and wants what they say next as the answer.</summary>
    Answer,

    /// <summary>It staged an action and wants a yes or a no about that action.</summary>
    Confirmation,
}

/// <summary>
/// One open window: what the bot is waiting for, until when, and what it is about.
/// </summary>
/// <param name="For">Which kind of reply is expected, which decides how it is read.</param>
/// <param name="Until">When to stop waiting.</param>
/// <param name="Token">
/// The grant a confirmation would redeem. The bot holds this only for as long as the window is open
/// and never acts on it without a clear yes; it is the same grant the button carries, so a voice
/// approval and a click are the same redemption and the second of them is refused.
/// </param>
/// <param name="Describes">The action in words, for asking about it again.</param>
/// <param name="Asked">
/// How many times the bot has now asked. A question it cannot get a clear answer to is asked a
/// bounded number of times and then left to the buttons — repeating forever is how a voice channel
/// becomes unusable, and the prompt in the chat has not gone anywhere.
/// </param>
public sealed record VoiceWaiting(
    VoiceWaitingFor For,
    DateTimeOffset Until,
    string? Token = null,
    string? Describes = null,
    int Asked = 1);
