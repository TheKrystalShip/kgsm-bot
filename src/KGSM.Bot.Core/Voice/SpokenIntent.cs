using System.Text;

namespace KGSM.Bot.Core.Voice;

/// <summary>What somebody meant when the bot asked them to confirm something.</summary>
public enum SpokenIntent
{
    /// <summary>Neither a yes nor a no that can be relied on. The only safe reading of everything else.</summary>
    Unclear,

    /// <summary>They agreed, in one of the ways people actually agree.</summary>
    Affirm,

    /// <summary>They declined, or asked to wait.</summary>
    Decline,
}

/// <summary>
/// Reads a spoken yes or no, and admits when it is neither.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three answers, and the third is the important one.</b> This gates a destructive action, so the
/// question is never "was that closer to yes or to no" — it is "was that unmistakably yes". Anything
/// else is <see cref="SpokenIntent.Unclear"/> and gets asked again. A binary classifier here would
/// have to resolve noise into one of two answers, and half the time it would resolve it into
/// approval.
/// </para>
/// <para>
/// <b>Deliberately no model.</b> A model would generalise better over phrasing, and it would also be
/// a thing that can be talked into approving — a component that decides whether to destroy something
/// should be readable, testable, and identical every time it runs. When this cannot tell, the answer
/// is to ask the person again, which costs a second and cannot go wrong.
/// </para>
/// <para>
/// <b>The two directions are judged differently on purpose.</b> Approving needs a short, clean
/// utterance that is plainly about the question; declining is looser, because a wrongly-read no
/// cancels something that can simply be asked for again, and a wrongly-read yes cannot be taken back.
/// Anything hedged is neither, however it leans: "probably" is not consent.
/// </para>
/// </remarks>
public static class SpokenIntents
{
    /// <summary>How much unrelated speech may surround a yes before it stops being an answer.</summary>
    /// <remarks>
    /// "Yeah, go ahead" is an answer. "Yeah, I was telling him about the minecraft thing" is somebody
    /// talking to a person in the room, and it contains a yes. The difference that survives is length:
    /// an answer to a yes-or-no question is short, and this is what stops a conversation approving
    /// things.
    /// </remarks>
    private const int AffirmSlack = 3;

    /// <summary>The same allowance for a no, which is wider because a wrong no costs nothing.</summary>
    private const int DeclineSlack = 6;

    /// <summary>
    /// Anything that makes an answer conditional. Checked first: several of these contain a "no" or a
    /// "don't" that would otherwise read as a decision.
    /// </summary>
    private static readonly string[] Hedges =
    [
        "not sure", "unsure", "i think so", "i think", "i guess", "i suppose", "i dont know",
        "dont know", "dunno", "no idea", "maybe", "perhaps", "probably", "possibly", "kind of",
        "sort of", "kinda", "sorta", "either way", "up to you", "whatever you think",
    ];

    /// <summary>
    /// Turning it down, or putting it off. Matched before the affirmations, so a phrase that contains
    /// one ("don't do it") is read as the refusal it is rather than as both at once.
    /// </summary>
    private static readonly string[] Declines =
    [
        "dont do it", "do not do it", "dont do that", "dont", "do not", "never mind", "nevermind",
        "forget it", "forget that", "leave it alone", "leave it", "not now", "not yet",
        "not right now", "hold on", "hold up", "hang on", "no thank you", "no thanks",
        "call it off", "back out", "on second thought", "actually no",
        "no", "nope", "nah", "naw", "negative", "cancel", "abort", "stop", "wait", "decline",
        "deny", "denied", "skip", "scratch that",
    ];

    /// <summary>
    /// Agreeing, in the ways people do it out loud. A vocabulary rather than a password: none of
    /// these is required, and one that is missing costs a second question, never a wrong action.
    /// </summary>
    private static readonly string[] Affirms =
    [
        "go ahead", "go for it", "go on", "do it", "do that", "send it", "make it so", "lets do it",
        "let us do it", "sounds good", "sounds right", "sounds fine", "thats right", "that is right",
        "that works", "carry on", "get on with it", "hit it", "run it", "fire away", "of course",
        "why not", "sure thing", "you bet", "please do", "if you would", "id like that",
        "yes", "yeah", "yep", "yup", "yah", "ya", "aye", "sure", "okay", "okey", "ok", "alright",
        "alrighty", "affirmative", "correct", "confirm", "confirmed", "confirming", "approve",
        "approved", "absolutely", "definitely", "certainly", "indeed", "proceed", "roger", "agreed",
        "agree", "please",
    ];

    /// <summary>Words that carry no decision and should not count against how short an answer is.</summary>
    private static readonly string[] Filler =
    [
        "um", "uh", "erm", "ah", "oh", "eh", "hmm", "well", "so", "like", "just", "then", "now",
        "there", "thanks", "thank you", "man", "mate", "dude", "buddy", "bro", "assistant",
        "hey assistant", "hey", "it", "that", "this", "one",
    ];

    /// <summary>
    /// Deliberately absent: the affirmative grunts.
    /// </summary>
    /// <remarks>
    /// "Uh-huh" means yes and "uh-uh" means no, and they differ by one vowel. A recogniser working
    /// from a noisy voice channel will confuse them, and the direction it confuses them in approves
    /// something. They are the one part of natural speech left out on purpose — asking again costs a
    /// second, and this is exactly the misrecognition the third outcome exists for.
    /// </remarks>
    private static readonly string[] Grunts = ["uh huh", "uhhuh", "mhm", "mhmm", "mm hmm", "mmhmm", "uh uh", "mm mm"];

    /// <summary>Reads what somebody said in answer to a confirmation.</summary>
    public static SpokenIntent Read(string? said)
    {
        if (string.IsNullOrWhiteSpace(said)) return SpokenIntent.Unclear;

        string text = Normalise(said);
        if (text.Length == 0) return SpokenIntent.Unclear;

        // A hedge is not a decision however it leans, and several of them read as one if the words
        // inside them are matched on their own.
        if (Hedges.Any(h => Contains(text, h))) return SpokenIntent.Unclear;

        // A grunt is a decision the recogniser cannot be trusted to have heard the right way round.
        if (Grunts.Any(g => Contains(text, g))) return SpokenIntent.Unclear;

        (string withoutDeclines, bool declined) = Strip(text, Declines);
        (string rest, bool affirmed) = Strip(withoutDeclines, Affirms);

        // Both at once is somebody talking, not answering — "yeah, no" is a real thing to say and it
        // is not a decision this may make on their behalf.
        if (declined && affirmed) return SpokenIntent.Unclear;
        if (!declined && !affirmed) return SpokenIntent.Unclear;

        int leftOver = Words(Strip(rest, Filler).Text);

        if (declined) return leftOver <= DeclineSlack ? SpokenIntent.Decline : SpokenIntent.Unclear;
        return leftOver <= AffirmSlack ? SpokenIntent.Affirm : SpokenIntent.Unclear;
    }

    /// <summary>Removes every phrase in <paramref name="phrases"/>, longest first so the specific wins.</summary>
    private static (string Text, bool Found) Strip(string text, string[] phrases)
    {
        var found = false;

        foreach (string phrase in phrases.OrderByDescending(p => p.Length))
        {
            while (Contains(text, phrase))
            {
                found = true;
                text = Remove(text, phrase);
            }
        }

        return (text, found);
    }

    /// <summary>Whether the text contains the phrase as whole words rather than inside longer ones.</summary>
    /// <remarks>
    /// Both sides are space-padded before the search, which is what keeps "no" out of "running" and
    /// "ya" out of "yesterday" — a substring match here would read half the language as a decision.
    /// </remarks>
    private static bool Contains(string text, string phrase) =>
        $" {text} ".Contains($" {phrase} ", StringComparison.Ordinal);

    private static string Remove(string text, string phrase) =>
        $" {text} ".Replace($" {phrase} ", " ", StringComparison.Ordinal).Trim();

    private static int Words(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Reduces speech to lowercase words, keeping the apostrophes out of contractions.</summary>
    private static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = true;

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (c is '\'' or '’')
            {
                // Dropped rather than kept, so "don't" and "dont" are the same word — a recogniser
                // spells it either way and the vocabulary should not have to carry both.
                continue;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().Trim();
    }
}
