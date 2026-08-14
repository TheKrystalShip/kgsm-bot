namespace KGSM.Bot.Core.Voice;

/// <summary>
/// The short thing said the moment a request is heard, while the real answer is still being worked
/// out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Silence reads as broken.</b> An assistant turn takes seconds and a confirmed install was
/// measured at thirty-two of them, all of it silent — from inside a voice channel that is
/// indistinguishable from a bot that did not hear you, and the natural thing to do is say it again.
/// A sentence said immediately costs nothing and answers the only question the person actually has
/// at that moment, which is whether they were heard.
/// </para>
/// <para>
/// <b>It is said alongside the work, never before it.</b> Speaking is not on the path to the answer:
/// the turn starts first and the audio plays while it runs, so this makes the first sound arrive
/// sooner without making the answer arrive later. Waiting for the acknowledgement to finish before
/// starting the work would add a second to every request to save the appearance of one.
/// </para>
/// <para>
/// <b>Varied, because the same words every time stop being information.</b> A phrase that never
/// changes is heard as a noise the bot makes rather than as a reply, and a person who has stopped
/// listening to it has lost the thing it was for.
/// </para>
/// </remarks>
public static class SpokenAcknowledgement
{
    private static readonly string[] Thinking =
    [
        "Looking into it.",
        "One moment.",
        "Let me check.",
        "Checking now.",
        "On it.",
        "Give me a second.",
    ];

    private static readonly string[] Starting =
    [
        "Alright, doing that now.",
        "On it.",
        "Right away.",
        "Doing that now.",
    ];

    private static readonly string[] StartingSomethingLong =
    [
        "Alright, that one takes a while. I'll tell you when it's done.",
        "On it — this'll take a few minutes. I'll let you know.",
        "Starting that now. It takes a bit, so I'll come back to you when it finishes.",
        "Right, that's a long one. I'll tell you as soon as it's finished.",
    ];

    /// <summary>
    /// The kinds of action worth warning somebody about before they start waiting.
    /// </summary>
    /// <remarks>
    /// These move files around — a download, an archive, a restore — and take tens of seconds at
    /// best. The lifecycle verbs are not here: starting and stopping answer quickly enough that a
    /// warning about the wait would be longer than the wait.
    /// </remarks>
    private static readonly string[] Slow =
        ["install", "uninstall", "update", "upgrade", "backup", "restore", "reinstall"];

    /// <summary>Something to say while a question is being worked out.</summary>
    public static string WhileThinking() => Pick(Thinking);

    /// <summary>
    /// Something to say the moment an approved action starts, warning about the wait when there is
    /// one worth warning about.
    /// </summary>
    public static string WhileWorking(string? kind) =>
        Pick(IsSlow(kind) ? StartingSomethingLong : Starting);

    /// <summary>Whether this kind of action is one somebody would otherwise wonder about.</summary>
    public static bool IsSlow(string? kind) =>
        kind is { Length: > 0 }
        && Slow.Any(s => kind.Contains(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One of them, at random.
    /// </summary>
    /// <remarks>
    /// Random rather than in turn: a rotation through a short list is a pattern people notice and
    /// start predicting, which is the thing the variation exists to avoid.
    /// </remarks>
    private static string Pick(string[] phrases) => phrases[Random.Shared.Next(phrases.Length)];
}
