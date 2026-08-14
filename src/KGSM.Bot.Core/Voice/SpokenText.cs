using System.Text;

namespace KGSM.Bot.Core.Voice;

/// <summary>
/// Turns a reply written for a chat window into something worth reading out loud.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a fallback, not the answer.</b> A reply that is short and conversational because that
/// is what was asked for needs nothing done to it; the real fix for a reply full of headings and code
/// is that it should not have been written that way for a surface that speaks. What this does is
/// keep the failure survivable — a stray backtick read as "backtick" is worse than one silently
/// dropped, and neither is as good as the assistant answering "yes, it is".
/// </para>
/// <para>
/// <b>Nothing is summarised.</b> Cutting to a sentence boundary under a budget is the one liberty
/// taken, and the full reply is in the channel either way — so what is spoken is always a prefix of
/// what was said, never a paraphrase of it. Rewording a reply on its way to being read out is how a
/// surface comes to say something the assistant did not.
/// </para>
/// </remarks>
public static class SpokenText
{
    /// <summary>
    /// Strips the markup a chat reply carries and trims it to <paramref name="maxCharacters"/> at a
    /// sentence boundary. Empty when there is nothing left worth speaking.
    /// </summary>
    public static string From(string? reply, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

        string plain = Plain(reply);
        return plain.Length <= maxCharacters ? plain : ToSentenceBoundary(plain, maxCharacters);
    }

    /// <summary>
    /// Removes fenced blocks whole, and markup characters in place.
    /// </summary>
    /// <remarks>
    /// A fenced block goes entirely rather than being flattened: a stack trace or a config file read
    /// aloud is a minute of noise that nobody can follow and that the reader cannot skip. What is
    /// inside it is in the channel, which is where somebody would go to read it anyway.
    /// </remarks>
    private static string Plain(string reply)
    {
        var builder = new StringBuilder(reply.Length);
        bool inFence = false;

        foreach (string line in reply.Split('\n'))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence) continue;

            builder.Append(Inline(trimmed)).Append(' ');
        }

        return Collapse(builder.ToString());
    }

    /// <summary>Drops the characters that carry formatting rather than meaning.</summary>
    private static string Inline(string line)
    {
        // A bullet or a heading marker is punctuation for the eye; spoken, it is either silence or
        // the word "hash". Leading list markers go; the text after them is a sentence.
        line = line.TrimStart('#', '>', '-', '*', '+', ' ');

        var builder = new StringBuilder(line.Length);
        foreach (char c in line)
        {
            // Emphasis and code markers are the ones a reader would voice as themselves. Everything
            // else — including punctuation a synthesiser uses for prosody — stays.
            if (c is '`' or '*' or '_' or '~' or '#') continue;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Cuts at the last sentence end within the budget, or at the last word if there is no sentence
    /// end to cut at — a reply stopped mid-word sounds like a fault rather than an abbreviation.
    /// </summary>
    private static string ToSentenceBoundary(string text, int max)
    {
        string window = text[..max];

        // Any sentence end beats any word end, however early it falls. Leaving budget unused is not
        // a cost worth paying for: what is on the other side of it is by definition an unbroken run
        // with no sentence in it, and a clean stop is better than a fragment of one.
        int sentence = window.LastIndexOfAny(['.', '!', '?']);
        if (sentence >= 0) return window[..(sentence + 1)].Trim();

        int word = window.LastIndexOf(' ');
        return (word > 0 ? window[..word] : window).Trim() + "…";
    }
}
