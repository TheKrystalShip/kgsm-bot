using Discord;
using Discord.Interactions;

using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.DependencyInjection;

namespace KGSM.Bot.Discord.Autocomplete;

/// <summary>
/// Suggests the voices this host actually has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Autocomplete rather than a fixed set of choices.</b> Discord allows twenty-five choices on an
/// option and there are more English voices than that before any other language is counted — a
/// choice list would have had to drop some, silently, with no way to ask for one that was cut.
/// </para>
/// <para>
/// <b>Read from the synthesiser, so it is what is installed</b> rather than what a list claims. An
/// empty result is the honest answer for a host with no speech: nothing to suggest, and the command
/// says why when it runs.
/// </para>
/// </remarks>
public class SpeechVoiceAutocompleteHandler : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var speech = services.GetRequiredService<ITextToSpeech>();
        string typed = autocompleteInteraction.Data.Current.Value?.ToString() ?? string.Empty;

        var matching = speech.Voices
            .Where(v => v.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(v => new AutocompleteResult(v, v))
            .ToList();

        return Task.FromResult(AutocompletionResult.FromSuccess(matching));
    }
}
