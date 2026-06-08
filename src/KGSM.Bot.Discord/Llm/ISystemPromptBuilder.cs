namespace KGSM.Bot.Discord.Llm;

/// <summary>
/// Builds the system prompt fresh each turn, injecting the live list of
/// instances and blueprints so the model can route directly and disambiguate.
/// </summary>
public interface ISystemPromptBuilder
{
    /// <param name="canPerformActions">
    /// Whether the requesting user is authorized to run mutating actions. Shapes
    /// what the model is told it can do.
    /// </param>
    Task<string> BuildAsync(bool canPerformActions, CancellationToken cancellationToken = default);
}
