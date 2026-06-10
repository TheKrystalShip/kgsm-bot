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

    public static string Confirm(PendingConfirmation c) => c.Kind switch
    {
        ConfirmationKind.Uninstall => $"{ConfirmPrefix}U{Sep}{c.Target}",
        ConfirmationKind.Install => $"{ConfirmPrefix}I{Sep}{c.Target}{Sep}{c.InstanceName}",
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    };

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
        switch (parts[0])
        {
            case "U" when parts.Length == 2 && parts[1].Length > 0:
                confirmation = new PendingConfirmation(ConfirmationKind.Uninstall, parts[1]);
                return true;
            case "I" when parts.Length == 3 && parts[1].Length > 0:
                var name = parts[2].Length == 0 ? null : parts[2];
                confirmation = new PendingConfirmation(ConfirmationKind.Install, parts[1], name);
                return true;
            default:
                return false;
        }
    }
}
