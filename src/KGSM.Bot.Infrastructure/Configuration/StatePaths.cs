namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Where the bot's own persistent state lives — today that is the guild store and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, in order.</b> A configured path that differs from the shipped default is an
/// operator naming a location deliberately, and wins. Otherwise the file sits in
/// <c>$STATE_DIRECTORY</c>, which systemd creates before <c>ExecStart</c> and chowns to the unit's
/// <c>User=</c> from <c>StateDirectory=kgsm-bot</c> — so the directory costs no privilege to
/// provision and follows the user the unit is templated with, whether or not that account has a
/// home directory.
/// </para>
/// <para>
/// Outside systemd — the <c>guilds</c> CLI subcommand, a test, a bot run from a terminal — there is
/// no <c>$STATE_DIRECTORY</c>, and the shipped default is used as it stands. Both name
/// <c>/var/lib/kgsm-bot</c> on a deployed host, so the store is the same file either way.
/// </para>
/// </remarks>
public static class StatePaths
{
    /// <summary>
    /// The directory the unit's <c>StateDirectory=kgsm-bot</c> resolves to, and the location every
    /// shipped default names. It sits outside <c>/opt/kgsm-bot</c> because the deploy syncs that
    /// prefix with <c>rsync --delete</c>.
    /// </summary>
    public const string DefaultDirectory = "/var/lib/kgsm-bot";

    /// <summary>
    /// The directory systemd provisioned for this unit, or <see cref="DefaultDirectory"/> when the
    /// process is not running under one.
    /// </summary>
    /// <remarks>
    /// <c>$STATE_DIRECTORY</c> is colon-separated when a unit declares several directories; this one
    /// declares a single directory, and the first entry is the bot's either way.
    /// </remarks>
    public static string Directory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("STATE_DIRECTORY") is not { Length: > 0 } value)
                return DefaultDirectory;

            string[] parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : DefaultDirectory;
        }
    }

    /// <summary>
    /// The path a state file is actually opened at.
    /// </summary>
    /// <param name="configured">The configured value, e.g. <c>Guilds:DbPath</c>.</param>
    /// <param name="shippedDefault">The default that value carries when nobody has set it.</param>
    /// <param name="fileName">The file's name inside <see cref="Directory"/>.</param>
    public static string Resolve(string? configured, string shippedDefault, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured, shippedDefault, StringComparison.Ordinal))
            return configured;

        return Path.Combine(Directory, fileName);
    }
}
