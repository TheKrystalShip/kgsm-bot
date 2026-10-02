using Discord;
using Discord.Interactions;

using KGSM.Bot.Application;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM;

namespace KGSM.Bot.Discord.Autocomplete;

/// <summary>
/// Autocomplete handler for blueprints, offered to whoever may read this node's library of them.
/// </summary>
public class BlueprintAutocompleteHandler : AutocompleteHandler
{
    private readonly IServerService _server;
    private readonly IBotAccess _access;
    private readonly ILogger<BlueprintAutocompleteHandler> _logger;

    public BlueprintAutocompleteHandler(
        IServerService server,
        IBotAccess access,
        ILogger<BlueprintAutocompleteHandler> logger)
    {
        _server = server;
        _access = access;
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
            _logger.LogDebug("Generating blueprint suggestions for autocomplete");

            // Get current value
            string currentValue = autocompleteInteraction.Data.Current.Value.ToString() ?? string.Empty;

            PersonAccess person = await _access.ResolveAsync(context.User.Id);
            if (!await person.AllowsAsync(KgsmActions.LibraryRead, null))
                return AutocompletionResult.FromSuccess();

            // Get all blueprints
            var result = await _server.GetAllBlueprintsAsync();
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to get blueprints for autocomplete: {Error}", result.ErrorMessage);
                return AutocompletionResult.FromError(new Exception(result.ErrorMessage));
            }

            // Filter blueprints by current value
            var filteredBlueprints = result.Blueprints!
                .Where(b => b.Key.Contains(currentValue, StringComparison.OrdinalIgnoreCase))
                .Select(b => new AutocompleteResult(b.Key, b.Key))
                .Take(25) // Discord has a limit of 25 autocomplete results
                .ToList();

            _logger.LogDebug("Generated {Count} blueprint suggestions for autocomplete", filteredBlueprints.Count);
            return AutocompletionResult.FromSuccess(filteredBlueprints);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating blueprint suggestions for autocomplete");
            return AutocompletionResult.FromError(ex);
        }
    }
}
