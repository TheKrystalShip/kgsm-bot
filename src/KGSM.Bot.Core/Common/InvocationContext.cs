namespace KGSM.Bot.Core.Common;

/// <summary>
/// The provenance of the action currently being performed — <em>who</em> triggered it
/// (<see cref="Actor"/>, e.g. <c>discord:haru</c>) and <em>through which surface</em>
/// (<see cref="Origin"/>, always <c>discord</c> for this bot). Flows to KGSM as
/// <c>$KGSM_EVENT_ACTOR</c>/<c>$KGSM_EVENT_ORIGIN</c> so the audit event the engine emits is attributable.
/// </summary>
public sealed record Invocation(string? Actor, string Origin)
{
    /// <summary>The bot's surface tag (architecture.html §3·d origin set).</summary>
    public const string DiscordOrigin = "discord";

    /// <summary>Build an invocation for a Discord user — <c>actor = discord:&lt;username&gt;</c>,
    /// <c>origin = discord</c>. A blank username yields a null actor (KGSM keeps its OS-user fallback,
    /// never a fabricated identity).</summary>
    public static Invocation ForDiscordUser(string? username) =>
        new(string.IsNullOrWhiteSpace(username) ? null : $"discord:{username}", DiscordOrigin);
}

/// <summary>
/// Ambient holder of the current <see cref="Invocation"/>, set at an entry point (a slash command method,
/// the LLM message handler) and read at the kgsm chokepoint (<c>KgsmServerInstanceService</c>) so every
/// mutating call can stamp provenance — without threading an actor through every command/handler/port.
/// </summary>
/// <remarks>
/// Backed by an <see cref="System.Threading.AsyncLocal{T}"/>, so the value flows down the async call chain
/// of the request that set it and is isolated per concurrent request. <see cref="Begin"/> returns a scope
/// that restores the previous value on dispose. <see cref="Current"/> is <see langword="null"/> outside any
/// scope (e.g. a background task) — the chokepoint then passes null and KGSM applies its honest fallback.
/// </remarks>
public interface IInvocationContext
{
    Invocation? Current { get; }
    IDisposable Begin(Invocation invocation);
}

/// <inheritdoc cref="IInvocationContext"/>
public sealed class AsyncLocalInvocationContext : IInvocationContext
{
    private static readonly AsyncLocal<Invocation?> _current = new();

    public Invocation? Current => _current.Value;

    public IDisposable Begin(Invocation invocation)
    {
        Invocation? previous = _current.Value;
        _current.Value = invocation;
        return new Scope(previous);
    }

    private sealed class Scope(Invocation? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
