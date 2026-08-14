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
/// <b>Nothing is summarised and nothing is cut short.</b> The whole reply is spoken: what is read out
/// is what was written, minus the markup that only means anything on a screen. Stopping an answer
/// part-way is the surface deciding somebody has heard enough, which is not a judgement it is in any
/// position to make — and rewording one on its way to being read out is how a surface comes to say
/// something the assistant did not.
/// </para>
/// <para>
/// <b>How long an answer is belongs to the assistant, not here.</b> A spoken turn already asks for a
/// reply written to be heard, and that is the thing to change if answers run long. Trimming at this
/// end would leave the reply in the room disagreeing with the reply in the channel, with nothing
/// anywhere to say which one was the answer.
/// </para>
/// </remarks>
public static class SpokenText
{
    /// <summary>
    /// Strips the markup a chat reply carries. Empty when there is nothing left worth speaking.
    /// </summary>
    public static string From(string? reply) =>
        string.IsNullOrWhiteSpace(reply) ? string.Empty : Plain(reply);

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
}
