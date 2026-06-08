namespace KGSM.Bot.Discord.Llm;

/// <summary>The kind of destructive operation awaiting confirmation.</summary>
public enum ConfirmationKind
{
    Uninstall,
    Install
}

/// <summary>
/// A destructive operation that has been resolved and staged, awaiting an
/// explicit human button-confirmation before it runs.
/// <para>
/// <see cref="Target"/> is the RESOLVED name (an existing instance for
/// <see cref="ConfirmationKind.Uninstall"/>, a known blueprint for
/// <see cref="ConfirmationKind.Install"/>) — never the model's raw argument.
/// </para>
/// </summary>
public sealed record PendingConfirmation(
    ConfirmationKind Kind,
    string Target,
    string? InstanceName = null);

/// <summary>
/// Ambient, per-turn sink for destructive operations staged during an agent run.
/// <para>
/// The library agent loop calls the tool dispatcher with only the tool call (no
/// per-turn context), so the dispatcher publishes staged confirmations here and
/// <c>ServerAssistant</c> drains them after the turn — without the library
/// needing any notion of "confirmation". Backed by an <see cref="System.Threading.AsyncLocal{T}"/>
/// so concurrently-handled messages stay isolated.
/// </para>
/// </summary>
public interface IConfirmationContext
{
    /// <summary>Starts a fresh per-turn scope. Dispose to clear it.</summary>
    IDisposable BeginTurn();

    /// <summary>Records a staged confirmation for the current turn (no-op outside a turn).</summary>
    void Stage(PendingConfirmation confirmation);

    /// <summary>Returns the confirmations staged in the current turn (empty outside a turn).</summary>
    IReadOnlyList<PendingConfirmation> Staged { get; }
}

/// <summary>
/// Builds and parses the Discord button <c>customId</c>s for confirmations. The
/// resolved action is encoded directly in the id (no server-side store needed);
/// the click handler still re-validates the target and re-authorizes the clicker.
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

/// <inheritdoc />
public sealed class ConfirmationContext : IConfirmationContext
{
    private static readonly AsyncLocal<List<PendingConfirmation>?> Current = new();

    public IDisposable BeginTurn()
    {
        Current.Value = new List<PendingConfirmation>();
        return new Scope();
    }

    public void Stage(PendingConfirmation confirmation) => Current.Value?.Add(confirmation);

    public IReadOnlyList<PendingConfirmation> Staged =>
        Current.Value is { } list ? list.ToArray() : Array.Empty<PendingConfirmation>();

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
