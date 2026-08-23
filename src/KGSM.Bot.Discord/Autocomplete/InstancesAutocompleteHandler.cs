using Discord;
using Discord.Interactions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Models;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Discord.Autocomplete;

/// <summary>
/// Autocomplete handler for instances
/// </summary>
public class InstancesAutocompleteHandler : AutocompleteHandler
{
    private readonly IServerService _server;
    private readonly ILogger<InstancesAutocompleteHandler> _logger;

    public InstancesAutocompleteHandler(
        IServerService server,
        ILogger<InstancesAutocompleteHandler> logger)
    {
        _server = server;
        _logger = logger;
    }

    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        try
        {
            _logger.LogDebug("Generating instance suggestions for autocomplete");

            // Get current value
            string currentValue = autocompleteInteraction.Data.Current.Value.ToString() ?? string.Empty;

            // Get all instances
            var result = await _server.GetAllInstancesAsync();
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to get instances for autocomplete: {Error}", result.ErrorMessage);
                return AutocompletionResult.FromError(new Exception(result.ErrorMessage));
            }

            // Matched on both names and shown as both. What is typed back is always the id: it is
            // what the command takes, what the engine keys on, and the one of the two that cannot
            // change under a person mid-interaction.
            var filteredInstances = result.Instances!
                .Where(i => i.Key.Contains(currentValue, StringComparison.OrdinalIgnoreCase)
                            || i.Value.DisplayName.Contains(currentValue, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(i => new AutocompleteResult(Describe(i.Key, i.Value.DisplayName), i.Key))
                .Take(25) // Discord has a limit of 25 autocomplete results
                .ToList();

            _logger.LogDebug("Generated {Count} instance suggestions for autocomplete", filteredInstances.Count);
            return AutocompletionResult.FromSuccess(filteredInstances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating instance suggestions for autocomplete");
            return AutocompletionResult.FromError(ex);
        }
    }

    /// <summary>
    /// One entry, as Discord shows it. Capped at the 100 characters a choice name may hold, from the
    /// front — the label is the part somebody is reading down the list for.
    /// </summary>
    private static string Describe(string id, string displayName)
    {
        string described = ServerLabel.Describe(id, displayName);
        return described.Length <= 100 ? described : described[..100];
    }
}
