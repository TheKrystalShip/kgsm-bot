namespace KGSM.Bot.Core.Models;

/// <summary>
/// How a server is written down for somebody to read.
/// </summary>
/// <remarks>
/// A server has two names and they answer different questions. The <b>id</b> is what every command,
/// channel binding, cgroup and audit row keys on, and it never changes. The <b>display name</b> is
/// decoration somebody chose, and it changes whenever they like. This is the one place the two are
/// composed, so every surface here writes them the same way round.
/// <para>
/// ⚠ <b>Nothing derived here is ever passed back to the engine.</b> A label is text to read; the id
/// is what a command carries, which is why <see cref="Describe"/> keeps it visible rather than
/// hiding it behind a prettier word.
/// </para>
/// </remarks>
public static class ServerLabel
{
    /// <summary>
    /// The label to show for a server: its display name, or its id when it has none of its own.
    /// </summary>
    /// <remarks>
    /// A blank display name is a server that was never labelled, not a nameless one — the id is the
    /// honest answer, and inventing a prettier one would name a server something nobody chose.
    /// </remarks>
    public static string Of(string id, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();

    /// <summary>
    /// The label with the id beside it, or the bare id when they are the same string.
    /// </summary>
    /// <remarks>
    /// Both are printed wherever the reader may need to type the id afterwards — an autocomplete
    /// entry, a listing, a message about a server they are about to act on. Repeating the id after
    /// itself is noise, so a server that was never labelled reads as its id alone.
    /// </remarks>
    public static string Describe(string id, string? displayName)
    {
        string label = Of(id, displayName);
        return string.Equals(label, id, StringComparison.Ordinal) ? id : $"{label} ({id})";
    }
}
