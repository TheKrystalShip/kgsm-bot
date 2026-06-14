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

    // Codes 'C' (SetConfig) and 'E' (overflow-store marker) are also RESERVED, even though
    // they live outside the Codes map above — SetConfig and the store are encoded bespoke
    // below. Do not reassign 'C'/'E' to another kind.
    public static string Confirm(PendingConfirmation c) => c.Kind switch
    {
        // Install (create): I~target~name.
        ConfirmationKind.Install => $"{ConfirmPrefix}I{Sep}{c.Target}{Sep}{c.InstanceName}",
        // SetConfig: C~target~key~value. The value is LAST so it may itself contain the
        // separator; TryParse re-splits with a limit of 4 to keep it whole.
        ConfirmationKind.SetConfig =>
            $"{ConfirmPrefix}C{Sep}{c.Target}{Sep}{c.ConfigKey}{Sep}{c.ConfigValue}",
        // Every other instance-targeted command: <code>~target.
        _ => $"{ConfirmPrefix}{Codes[c.Kind]}{Sep}{c.Target}",
    };

    /// <summary>
    /// customId for a confirmation whose payload is too long to ride the id (a long
    /// SetConfig value). The resolved op is held in a <c>PendingEditStore</c> under
    /// <paramref name="storeId"/>; the click handler looks it up via <see cref="TryParseStored"/>.
    /// </summary>
    public static string ConfirmStored(string storeId) => $"{ConfirmPrefix}E{Sep}{storeId}";

    /// <summary>
    /// True if <paramref name="data"/> is a store-backed confirmation (<c>E~id</c>),
    /// yielding the store id to look up. Kept separate from <see cref="TryParse"/> because
    /// the payload lives server-side, not in the id.
    /// </summary>
    public static bool TryParseStored(string data, out string storeId)
    {
        storeId = string.Empty;
        if (string.IsNullOrEmpty(data))
            return false;

        var parts = data.Split(Sep, 2);
        if (parts.Length == 2 && parts[0] == "E" && parts[1].Length > 0)
        {
            storeId = parts[1];
            return true;
        }
        return false;
    }

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

        // SetConfig: C~target~key~value. Re-split with a limit so a value containing the
        // separator stays whole (it is the final segment). An empty value clears the key.
        if (parts[0] == "C")
        {
            var seg = data.Split(Sep, 4);
            if (seg.Length == 4 && seg[1].Length > 0 && seg[2].Length > 0)
            {
                confirmation = new PendingConfirmation(
                    ConfirmationKind.SetConfig, seg[1], InstanceName: null,
                    ConfigKey: seg[2], ConfigValue: seg[3]);
                return true;
            }
            return false;
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
