using System.Text;

namespace KGSM.Bot.Core.Voice;

/// <summary>
/// Cuts a reply into the pieces it can be read out loud in, as it is being written.
/// </summary>
/// <remarks>
/// <para>
/// <b>A model writes an answer over seconds, and a sentence of it is worth speaking the moment it is
/// finished.</b> Slices arrive as token fragments — half a word at a time — so this holds them until
/// there is a whole sentence and hands that over. What is spoken is still the whole reply in the
/// order it was written: this decides <em>when</em> each part of it goes out, never what is in it.
/// </para>
/// <para>
/// ⚠ <b>A fenced code block spans sentences, so nothing is cut inside one.</b> Stripping markup per
/// sentence would read a stack trace out a line at a time — the very thing <see cref="SpokenText"/>
/// exists to prevent, defeated by segmenting first. So the fence is tracked across the slices as they
/// accumulate and no boundary is offered between its two markers; the piece that is finally cut
/// carries the whole block, and <see cref="SpokenText"/> drops it exactly as it does for a reply
/// spoken in one go. A fence the reply never closes is dropped the same way at the flush.
/// </para>
/// <para>
/// <b>A boundary needs a full stop and a length.</b> "Yes." on its own is a recital of two syllables
/// with the whole cost of a synthesis call and a hand-off around it, and a run of those is worse to
/// listen to than one sentence — so a piece is cut at the first sentence end past
/// <see cref="LeastChars"/> and short sentences ride along with the next one.
/// </para>
/// <para>
/// <b>Whatever is left at the end is spoken.</b> A reply that never reaches a full stop, or ends on
/// one with nothing after it, is not a reply to leave unsaid: <see cref="Rest"/> is the flush, and the
/// caller owes it one call.
/// </para>
/// </remarks>
public sealed class SpokenSegmenter
{
    /// <summary>
    /// How much a piece must be worth, in characters of speech, before a sentence end will cut it.
    /// </summary>
    public const int LeastChars = 40;

    private readonly StringBuilder _pending = new();

    /// <summary>
    /// Takes the next slice of the reply and returns whatever is now whole enough to say, in order.
    /// </summary>
    /// <remarks>Empty is the ordinary answer: most slices are the middle of a sentence.</remarks>
    public IReadOnlyList<string> Wrote(string? slice)
    {
        if (string.IsNullOrEmpty(slice)) return [];

        _pending.Append(slice);

        List<string>? ready = null;
        while (Cut() is { } sentence)
            (ready ??= []).Add(sentence);

        return ready ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Everything held back, said as one last piece. Empty when the reply ended on a boundary.
    /// </summary>
    /// <remarks>
    /// The segmenter is empty afterwards, so a second call answers nothing rather than repeating what
    /// was already spoken.
    /// </remarks>
    public string Rest()
    {
        string rest = SpokenText.From(_pending.ToString());
        _pending.Clear();
        return rest;
    }

    /// <summary>
    /// Takes the next whole piece off the front, already stripped of markup. Null when there is not
    /// yet a boundary worth cutting at.
    /// </summary>
    private string? Cut()
    {
        string text = _pending.ToString();

        foreach (int at in Boundaries(text))
        {
            string spoken = SpokenText.From(text[..at]);

            // Too little to be worth its own recital — keep looking for a later boundary, which is
            // what makes a one-word sentence ride out with the one after it.
            if (spoken.Length < LeastChars) continue;

            _pending.Remove(0, at);
            return spoken;
        }

        return null;
    }

    /// <summary>
    /// Every place <paramref name="text"/> could be cut, in order: the exclusive end of a piece.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full stop, question mark or exclamation mark <em>followed by whitespace</em> ends a sentence;
    /// requiring the whitespace is what keeps "3.5 GB" and a version number in one piece, and it is
    /// why a terminator sitting at the very end of what has arrived so far is not a boundary — the
    /// next slice decides whether it was one, and the flush covers it if the reply simply stopped.
    /// </para>
    /// <para>
    /// A line ending is a boundary too: a reply's bullets and headings are separate breaths, and
    /// waiting for a full stop that a list item does not carry would hold the whole list back.
    /// </para>
    /// <para>
    /// ⚠ <b>Nothing inside a fence is offered</b>, including the newline that opens one. Cutting there
    /// would leave the block's contents starting a fresh buffer with the fence forgotten, which is
    /// precisely how a config file gets read out a line at a time.
    /// </para>
    /// </remarks>
    private static IEnumerable<int> Boundaries(string text)
    {
        bool fenced = false;

        for (int i = 0; i < text.Length; i++)
        {
            // A fence marker is only a fence marker at the start of a line. The buffer's own start
            // counts as one: a piece is only ever cut where no fence is open, so what is left behind
            // always begins outside one.
            if ((i == 0 || text[i - 1] == '\n') && OpensOrCloses(text, i))
                fenced = !fenced;

            if (fenced) continue;

            char c = text[i];

            if (c == '\n')
            {
                yield return i + 1;
                continue;
            }

            if (c is not ('.' or '!' or '?')) continue;
            if (i + 1 >= text.Length || !char.IsWhiteSpace(text[i + 1])) continue;

            yield return i + 1;
        }
    }

    /// <summary>Whether the line beginning at <paramref name="at"/> is a fence marker.</summary>
    private static bool OpensOrCloses(string text, int at)
    {
        int i = at;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;

        return text.AsSpan(i).StartsWith("```", StringComparison.Ordinal);
    }
}
