namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// The outcome of an assistant turn: the reply text to show the user, plus any
/// destructive operations that were staged this turn and now need explicit human
/// confirmation before they run.
/// </summary>
public sealed record AssistantResult
{
    public bool IsSuccess { get; private init; }
    public string? Error { get; private init; }
    public string Text { get; private init; } = string.Empty;
    public IReadOnlyList<PendingConfirmation> Confirmations { get; private init; } =
        Array.Empty<PendingConfirmation>();

    public bool IsFailure => !IsSuccess;

    public static AssistantResult Ok(string text, IReadOnlyList<PendingConfirmation> confirmations) =>
        new() { IsSuccess = true, Text = text, Confirmations = confirmations };

    public static AssistantResult Fail(string error) =>
        new() { IsSuccess = false, Error = error };
}

/// <summary>
/// The kgsm-specific entry point into the LLM agent. Owns the application policy
/// — which tools to offer, the per-message action cap, authorization, and staging
/// destructive ops for confirmation — then hands a fully-formed turn to the
/// reusable library agent loop. Stateful only through the conversation store and
/// the per-turn confirmation scope; safe to share as a singleton.
/// </summary>
public interface IServerAssistant
{
    /// <param name="canPerformActions">
    /// Whether the requesting user is authorized to run mutating/destructive actions.
    /// When false, those tools are neither offered nor executed.
    /// </param>
    Task<AssistantResult> RunAsync(
        ulong userId,
        ulong channelId,
        string userPrompt,
        bool canPerformActions,
        CancellationToken cancellationToken = default);
}
