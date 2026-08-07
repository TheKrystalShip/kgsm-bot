namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// The Discord button ids for an action the kgsm-assistant leaf staged.
/// </summary>
/// <remarks>
/// <para>
/// The button carries the assistant's grant and nothing else. Discord echoes a component's
/// <c>customId</c> back on click and nothing more, so whatever must survive from proposal to
/// approval has to fit in 100 characters — which is exactly why the assistant hands out a
/// fixed-length handle instead of the operation itself. There is nothing here to encode, parse or
/// keep append-only: the id is the grant, and only the assistant can say what it means.
/// </para>
/// <para>
/// Its own prefix, separate from the ids the bot mints for actions it runs itself, so a button can
/// only ever be answered by the half of the bot that knows what to do with it.
/// </para>
/// </remarks>
public static class AssistantConfirmationIds
{
    /// <summary>Discord caps a component customId at 100 characters.</summary>
    public const int MaxCustomIdLength = 100;

    /// <summary>Prefix + wildcard segment the assistant-confirm handler matches on.</summary>
    public const string ConfirmPrefix = "kgsmact~";

    /// <summary>
    /// The Cancel button, which carries no data.
    /// </summary>
    /// <remarks>
    /// Deliberately outside <see cref="ConfirmPrefix"/>: the confirm handler matches
    /// <c>kgsmact~*</c>, so a cancel id living under that prefix would be captured by the wildcard
    /// and read as a grant.
    /// </remarks>
    public const string Cancel = "kgsmacx";

    /// <summary>The button that redeems <paramref name="token"/>.</summary>
    public static string Confirm(string token) => ConfirmPrefix + token;

    /// <summary>
    /// Whether a grant fits a button at all. A handle that has outgrown the cap must be reported,
    /// not truncated — a truncated grant is one Discord accepts and the assistant then refuses.
    /// </summary>
    public static bool Fits(string token) =>
        !string.IsNullOrEmpty(token) && ConfirmPrefix.Length + token.Length <= MaxCustomIdLength;
}
