namespace KGSM.Bot.Core.Voice;

/// <summary>Something somebody asked of the conversation itself, rather than of the assistant.</summary>
public enum SpokenConversationCommand
{
    /// <summary>An ordinary request. Everything that is not one of the two below.</summary>
    None,

    /// <summary>Forget what has been said and carry on from nothing.</summary>
    Clear,

    /// <summary>Fold what has been said into a summary and keep going from that.</summary>
    Compact,
}

/// <summary>
/// Recognises the two things a person can say <em>about</em> the conversation instead of into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read here rather than by the assistant, because the assistant is what they act on.</b> A model
/// asked to forget the conversation answers that it has, and remembers every word — the reply is the
/// only thing it can produce, and the reply is not the thing being asked for. Clearing and compacting
/// happen to the stored conversation, so they are read before a turn is dispatched and never become
/// one.
/// </para>
/// <para>
/// <b>Deliberately no model, for the same reason the yes/no reader has none</b>
/// (<see cref="SpokenIntents"/>): a phrase that discards a room's memory should be readable, testable
/// and identical every time it runs. What it costs is that only these phrasings work; what it buys is
/// that nothing else ever does.
/// </para>
/// <para>
/// <b>Matched against the WHOLE utterance, trigger stripped.</b> These are short things people say on
/// their own — "hey assistant, start over" — and a sentence that merely contains "start over"
/// somewhere in the middle of a request is a request. Somebody who says "start over, and then tell me
/// what's running" is asking two things, and answering the first is the safe half.
/// </para>
/// </remarks>
public static class SpokenConversationCommands
{
    /// <summary>
    /// Forgetting entirely. Every one of these is somebody asking for a blank slate rather than for
    /// something to be tidied — "forget it" is deliberately absent, because it far more often means
    /// "never mind" about the last thing than "discard everything we have said".
    /// </summary>
    private static readonly string[] Clearing =
    [
        "start over",
        "start again",
        "start fresh",
        "start a new conversation",
        "new conversation",
        "fresh conversation",
        "clear the conversation",
        "clear our conversation",
        "clear this conversation",
        "clear the chat",
        "reset the conversation",
        "reset our conversation",
        "forget everything",
        "forget all that",
        "forget what we talked about",
        "forget our conversation",
        "forget this conversation",
        "wipe the conversation",
    ];

    /// <summary>
    /// Keeping the thread but shortening it. Rarer to say out loud than to type, and offered because
    /// somebody who knows the difference should be able to ask for the gentler one.
    /// </summary>
    private static readonly string[] Compacting =
    [
        "compact the conversation",
        "compact our conversation",
        "compact this conversation",
        "summarize the conversation",
        "summarise the conversation",
        "summarize our conversation",
        "summarise our conversation",
        "condense the conversation",
    ];

    /// <summary>
    /// What <paramref name="said"/> asks of the conversation, or
    /// <see cref="SpokenConversationCommand.None"/> when it asks nothing of it.
    /// </summary>
    public static SpokenConversationCommand Read(string? said)
    {
        if (string.IsNullOrWhiteSpace(said)) return SpokenConversationCommand.None;

        string plain = Plain(said);
        if (plain.Length == 0) return SpokenConversationCommand.None;

        // Compacting is looked for first. Every clearing phrase is a superset of nothing here, but
        // "summarise the conversation and start over" leans the safe way when it is: folding what was
        // said keeps it, and discarding it does not.
        if (Matches(plain, Compacting)) return SpokenConversationCommand.Compact;
        if (Matches(plain, Clearing)) return SpokenConversationCommand.Clear;

        return SpokenConversationCommand.None;
    }

    /// <summary>
    /// Whether the utterance IS one of these phrases, allowing for the politeness people put around a
    /// short instruction — "please start over", "can you start over" — and nothing more.
    /// </summary>
    private static bool Matches(string plain, string[] phrases)
    {
        foreach (string phrase in phrases)
        {
            if (plain == phrase) return true;

            // The phrase at the end of a short lead-in. Bounded, because the whole point is that a
            // long sentence which happens to contain these words is a request and not a command.
            if (plain.EndsWith(' ' + phrase, StringComparison.Ordinal)
                && plain.Length - phrase.Length <= 20)
                return true;
        }

        return false;
    }

    /// <summary>Lower-cased, stripped of punctuation and of the filler that ends a spoken sentence.</summary>
    private static string Plain(string said)
    {
        var builder = new System.Text.StringBuilder(said.Length);
        bool space = false;

        foreach (char c in said)
        {
            if (char.IsLetter(c) || char.IsDigit(c))
            {
                if (space && builder.Length > 0) builder.Append(' ');
                space = false;
                builder.Append(char.ToLowerInvariant(c));
                continue;
            }

            // Everything else — punctuation the recogniser added, the spaces between words — is one
            // separator. A trailing full stop must not stop "start over." from being "start over".
            space = true;
        }

        return builder.ToString();
    }
}
