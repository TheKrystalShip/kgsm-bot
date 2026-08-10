namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// The Discord button ids for an action the bot runs itself, offered beside the announcement that
/// makes it worth running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own prefix, separate from the assistant's.</b> An assistant button carries an opaque grant
/// the bot holds no part of; one of these carries a server name and is executed here, through the
/// same path the slash command uses. Two kinds of button that mean different things must never share
/// a prefix — the wildcard handler that matched first would read the other one's payload as its own.
/// </para>
/// <para>
/// The id carries the instance name and nothing else. There is no staged operation to expire and
/// nothing to redeem: the button is a shortcut to <c>/restart &lt;server&gt;</c>, authorized at the
/// click like any other command, so a button posted before a restart still works after one.
/// </para>
/// <para>
/// It lives here rather than beside the module that handles it because the announcement path — which
/// mints the button — is in this assembly, and the id is the one thing both halves must agree on.
/// </para>
/// </remarks>
public static class ServerActionIds
{
    /// <summary>Discord caps a component customId at 100 characters.</summary>
    public const int MaxCustomIdLength = 100;

    /// <summary>Prefix + wildcard segment the restart handler matches on.</summary>
    public const string RestartPrefix = "kgsmsrv~restart~";

    /// <summary>The button that restarts <paramref name="instanceName"/>.</summary>
    public static string Restart(string instanceName) => RestartPrefix + instanceName;

    /// <summary>
    /// Whether a server's name fits a button. A name that does not is reported by leaving the button
    /// off rather than by posting a truncated one, which would act on a different server or on none.
    /// </summary>
    public static bool Fits(string instanceName) =>
        !string.IsNullOrEmpty(instanceName)
        && RestartPrefix.Length + instanceName.Length <= MaxCustomIdLength;
}
