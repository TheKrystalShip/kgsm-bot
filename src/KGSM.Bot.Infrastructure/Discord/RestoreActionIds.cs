namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// The Discord button ids for a restore this bot staged and is waiting to be told to run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own prefix, separate from the restart buttons and from the assistant's.</b> Two kinds of
/// button that mean different things must never share one: the wildcard handler that matched first
/// would read the other's payload as its own, and here that would be a destructive action taking a
/// server name from a message about something else.
/// </para>
/// <para>
/// The id carries a handle and nothing else. A restart button can carry a server name because it is
/// only a shortcut to a command anyone could type; a restore names a specific archive to overwrite a
/// server with, which does not reliably fit in 100 characters and must not be truncated into naming
/// a different one.
/// </para>
/// </remarks>
public static class RestoreActionIds
{
    /// <summary>Discord caps a component customId at 100 characters.</summary>
    public const int MaxCustomIdLength = 100;

    /// <summary>Prefix + wildcard segment the confirm handler matches on.</summary>
    public const string ConfirmPrefix = "kgsmrst~";

    /// <summary>
    /// The Cancel button. Deliberately outside <see cref="ConfirmPrefix"/> so the confirm handler's
    /// wildcard cannot capture it and read the word "cancel" as a handle.
    /// </summary>
    public const string CancelPrefix = "kgsmrsx~";

    /// <summary>The button that runs the restore held under <paramref name="handle"/>.</summary>
    public static string Confirm(string handle) => ConfirmPrefix + handle;

    /// <summary>The button that drops it.</summary>
    public static string Cancel(string handle) => CancelPrefix + handle;

    /// <summary>Whether a handle fits a button at all.</summary>
    public static bool Fits(string handle) =>
        !string.IsNullOrEmpty(handle) && ConfirmPrefix.Length + handle.Length <= MaxCustomIdLength;
}
