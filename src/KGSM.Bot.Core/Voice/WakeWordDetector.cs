using System.Text;

namespace KGSM.Bot.Core.Voice;

/// <summary>
/// Decides whether something said in a voice channel was addressed to the bot, and what was asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>This matches a transcript, not a sound.</b> There is no wake-word model here: everything said
/// is recognised anyway, and the trigger is found in the text that comes back. That makes
/// <em>consistency</em> the property a good trigger needs rather than accuracy — a phrase the
/// recogniser reliably mangles the same way is matchable, and one it renders differently each time is
/// not, whichever is closer to what was actually said. It is also why the trigger is a list: an
/// operator who sees a variant in the log adds it, with no model to retrain and nothing to rebuild.
/// </para>
/// <para>
/// <b>The trigger is found anywhere in what was said, and the request is whatever follows it.</b>
/// People lead into addressing an assistant — "okay, let me try this — hey assistant, restart
/// factorio" is one breath and therefore one utterance, and requiring the trigger to come first
/// refuses it. Utterance boundaries here are drawn by silence, which is a fact about this pipeline
/// and not about how anybody speaks; a device that listens continuously has no notion of what came
/// before its wake word, and neither should this.
/// </para>
/// <para>
/// The cost is a phrase like "so I said hey assistant and nothing happened", which is somebody
/// quoting the trigger and is answered as though it were an instruction. That is the better failure
/// by a wide margin: quoting a wake word is rare, leading into a sentence is what everyone does, and
/// a bot that ignores genuine requests is broken in a way an occasional spurious answer is not.
/// </para>
/// <para>
/// <b>The last occurrence wins.</b> Somebody who starts again — "hey assistant, no wait, hey
/// assistant, restart factorio" — means the second one, and everything before it is throat-clearing.
/// </para>
/// <para>
/// Matching is done on a normalised copy while the answer is built from the original words, so the
/// recogniser's punctuation and capitalisation cannot break a match, and a server called
/// <c>Ketchup</c> still reaches the assistant with its capital letter.
/// </para>
/// </remarks>
public sealed class WakeWordDetector
{
    /// <summary>
    /// Punctuation that can sit at either end of a request without being part of it.
    /// </summary>
    /// <remarks>
    /// Quotation marks are here because a recogniser writes them: somebody saying the trigger inside
    /// a quoted phrase gets one on the end of their request, and it reaches the assistant as part of
    /// a server's name. The apostrophe is deliberately absent — it ends real words.
    /// </remarks>
    private static readonly char[] RequestEdges =
        [' ', ',', '.', '!', '?', '-', ':', ';', '"', '“', '”'];

    private readonly string[][] _triggers;

    /// <param name="triggers">
    /// The phrases that address the bot. Each is matched as a sequence of words, so spacing and
    /// punctuation in either the trigger or the transcript are irrelevant.
    /// </param>
    public WakeWordDetector(IEnumerable<string> triggers)
    {
        _triggers = [.. triggers
            .Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize)
                .Where(w => w.Length > 0)
                .ToArray())
            .Where(words => words.Length > 0)
            // Longest first, so a trigger that is a prefix of another cannot claim the match and
            // leave the rest of its own trigger sitting at the front of the command.
            .OrderByDescending(words => words.Length)];
    }

    /// <summary>
    /// Returns what was asked when <paramref name="transcript"/> opens with a trigger, and null when
    /// it was not addressed to the bot.
    /// </summary>
    /// <remarks>
    /// An empty command is a real answer, not a failure: somebody said the trigger and nothing else,
    /// which is a person waiting to be listened to rather than a person asking for nothing.
    /// </remarks>
    public string? Match(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        string[] words = transcript.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Normalised once: the inner loop would otherwise re-normalise the same word for every
        // trigger and every start position it appears at.
        string[] normalised = [.. words.Select(Normalize)];

        int bestEnd = -1;
        foreach (string[] trigger in _triggers)
        {
            for (int start = normalised.Length - trigger.Length; start >= 0; start--)
            {
                bool matched = true;
                for (int i = 0; i < trigger.Length && matched; i++)
                    matched = normalised[start + i] == trigger[i];

                if (!matched) continue;

                // Latest wins across every trigger, not merely within one, so a long trigger early in
                // the sentence cannot beat a short one said later — the later one is the one somebody
                // meant.
                bestEnd = Math.Max(bestEnd, start + trigger.Length);
                break;
            }
        }

        if (bestEnd < 0) return null;

        return string.Join(' ', words[bestEnd..]).Trim(RequestEdges);
    }

    /// <summary>
    /// Reduces a word to the letters and digits in it, lower-cased.
    /// </summary>
    /// <remarks>
    /// Recognisers punctuate on prosody, so the same phrase comes back as "Hey assistant", "Hey,
    /// assistant" or "Hey assistant!" depending on how it was said. None of that is a different
    /// thing to have said.
    /// </remarks>
    private static string Normalize(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (char c in word)
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));

        return builder.ToString();
    }
}
