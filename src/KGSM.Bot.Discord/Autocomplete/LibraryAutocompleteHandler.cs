using Discord;
using Discord.Interactions;

using KGSM.Bot.Application;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Discord.Autocomplete;

/// <summary>
/// Autocomplete handler for the libraries an instance can be installed into.
/// </summary>
public class LibraryAutocompleteHandler : AutocompleteHandler
{
    private readonly IServerService _server;
    private readonly ILogger<LibraryAutocompleteHandler> _logger;

    public LibraryAutocompleteHandler(
        IServerService server,
        ILogger<LibraryAutocompleteHandler> logger)
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
            _logger.LogDebug("Generating library suggestions for autocomplete");

            string currentValue = autocompleteInteraction.Data.Current.Value.ToString() ?? string.Empty;

            var result = await _server.GetLibrariesAsync();
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to get libraries for autocomplete: {Error}", result.ErrorMessage);
                return AutocompletionResult.FromError(new Exception(result.ErrorMessage));
            }

            // Offline libraries are left out rather than shown greyed: Discord has no disabled
            // autocomplete entry, so offering one would be offering a placement the engine refuses.
            var suggestions = result.Libraries!
                .Where(l => l.Online)
                .Where(l => l.Name.Contains(currentValue, StringComparison.OrdinalIgnoreCase))
                .Select(l => new AutocompleteResult(Describe(l), l.Name))
                .Take(25) // Discord has a limit of 25 autocomplete results
                .ToList();

            _logger.LogDebug("Generated {Count} library suggestions for autocomplete", suggestions.Count);
            return AutocompletionResult.FromSuccess(suggestions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating library suggestions for autocomplete");
            return AutocompletionResult.FromError(ex);
        }
    }

    // Free space is what the choice actually turns on. It is omitted rather than shown as 0 when
    // the engine reported none, because an unmeasured library is not an empty one.
    private static string Describe(TheKrystalShip.KGSM.Core.Models.Library library)
        => library.FreeBytes is long free
            ? $"{library.Name} ({FormatBytes(free)} free)"
            : library.Name;

    private static string FormatBytes(long bytes)
    {
        const double Gib = 1024d * 1024d * 1024d;
        double gib = bytes / Gib;
        return gib >= 1024
            ? $"{gib / 1024:0.#} TiB"
            : $"{gib:0.#} GiB";
    }
}
