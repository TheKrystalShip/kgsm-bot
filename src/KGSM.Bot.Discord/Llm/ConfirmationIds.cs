using TheKrystalShip.Kgsm.Assistant;

namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// Builds and parses the Discord button <c>customId</c>s for confirmations. The
/// resolved action is encoded directly in the id (no server-side store needed);
/// the click handler still re-validates the target and re-authorizes the clicker.
/// This is the Discord-transport encoding for the assistant's
/// <see cref="PendingConfirmation"/>, so it stays in the bot.
/// </summary>
public static class ConfirmationIds
{
    /// <summary>Discord caps a component customId at 100 characters.</summary>
    public const int MaxCustomIdLength = 100;

    /// <summary>customId for the Cancel button (carries no data).</summary>
    public const string Cancel = "kgsmcx";

    /// <summary>Prefix + wildcard segment that the confirm handler matches on.</summary>
    public const string ConfirmPrefix = "kgsmcf~";

    private const char Sep = '~';

    /// <summary>
    /// Stable one-char code per kind. APPEND-ONLY: never reuse or reassign a letter — a
    /// posted button carries the old code until clicked. Every instance-targeted command
    /// encodes as <c>&lt;code&gt;~&lt;target&gt;</c>; <see cref="ConfirmationKind.Install"/>
    /// is special-cased (it carries an extra instance-name segment).
    /// </summary>
    private static readonly IReadOnlyDictionary<ConfirmationKind, char> Codes =
        new Dictionary<ConfirmationKind, char>
        {
            [ConfirmationKind.Uninstall] = 'U',
            [ConfirmationKind.Install] = 'I',
            [ConfirmationKind.Start] = 'S',
            [ConfirmationKind.Stop] = 'T',
            [ConfirmationKind.Restart] = 'R',
            [ConfirmationKind.Update] = 'P',
            [ConfirmationKind.Backup] = 'B',
        };

    private static readonly IReadOnlyDictionary<char, ConfirmationKind> ByCode =
        Codes.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string Confirm(PendingConfirmation c) => c.Kind == ConfirmationKind.Install
        ? $"{ConfirmPrefix}I{Sep}{c.Target}{Sep}{c.InstanceName}"
        : $"{ConfirmPrefix}{Codes[c.Kind]}{Sep}{c.Target}";

    /// <summary>
    /// Parses the wildcard remainder (everything after <see cref="ConfirmPrefix"/>)
    /// back into a <see cref="PendingConfirmation"/>. Returns false on malformed input.
    /// </summary>
    public static bool TryParse(string data, out PendingConfirmation confirmation)
    {
        confirmation = null!;
        if (string.IsNullOrEmpty(data))
            return false;

        var parts = data.Split(Sep);

        // Install (create): I~target~name (name segment may be empty → null).
        if (parts[0] == "I" && parts.Length == 3 && parts[1].Length > 0)
        {
            var name = parts[2].Length == 0 ? null : parts[2];
            confirmation = new PendingConfirmation(ConfirmationKind.Install, parts[1], name);
            return true;
        }

        // Every instance-targeted command: <code>~target.
        if (parts.Length == 2 && parts[0].Length == 1
            && ByCode.TryGetValue(parts[0][0], out var kind) && kind != ConfirmationKind.Install
            && parts[1].Length > 0)
        {
            confirmation = new PendingConfirmation(kind, parts[1]);
            return true;
        }

        return false;
    }
}
