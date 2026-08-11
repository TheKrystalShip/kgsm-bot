namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Whether the things this bot depends on are answering — measured, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is checked at the moment it is asked for. Nothing is remembered between calls and
/// no check is derived from another: a bot can be connected to Discord with an unreadable account
/// store, or hold a perfectly good account store while the engine is gone, and a summary that
/// inferred either from the other would hide exactly the state somebody is diagnosing.
/// </para>
/// <para>
/// Each check costs something small — a field read, a socket probe, one kgsm process — which is why
/// this is asked for by a person rather than run on a timer.
/// </para>
/// </remarks>
public interface IBotHealth
{
    /// <summary>Checks everything, in order, and reports each answer as itself.</summary>
    Task<IReadOnlyList<HealthCheck>> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// What one check found.
/// </summary>
/// <param name="Name">What was checked, in the words an operator would use for it.</param>
/// <param name="Verdict">Which of the four answers this is.</param>
/// <param name="Detail">
/// What was measured, or why it could not be. Always says something — a check with a verdict and no
/// account of it is not diagnosable.
/// </param>
public sealed record HealthCheck(string Name, HealthVerdict Verdict, string Detail);

/// <summary>
/// The four answers a check can give.
/// </summary>
/// <remarks>
/// Four rather than two, because collapsing them is how a health page starts lying. A dependency this
/// host was never given is not broken, and a check that could not be run is not a pass — reporting
/// either as <see cref="Ok"/> hides a real gap, and reporting either as <see cref="Failing"/> sends
/// somebody looking for a fault that is not there.
/// </remarks>
public enum HealthVerdict
{
    /// <summary>Checked, and answering.</summary>
    Ok,

    /// <summary>Checked, and not answering. The detail says what happened.</summary>
    Failing,

    /// <summary>Deliberately not configured on this host. Absence is the setting, not a fault.</summary>
    Off,

    /// <summary>The check could not reach an answer either way.</summary>
    Unknown,
}
