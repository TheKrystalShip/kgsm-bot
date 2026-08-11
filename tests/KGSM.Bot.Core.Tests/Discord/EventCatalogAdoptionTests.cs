using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Commands;

using TheKrystalShip.KGSM.Events;

using Xunit;

namespace KGSM.Bot.Core.Tests.Discord;

/// <summary>
/// The bot answers no question the engine already answers.
/// </summary>
/// <remarks>
/// <para>
/// This surface used to keep its own opinion about which payload fields identify somebody, and had no
/// opinion at all about which events are steps inside a larger operation. Both live in
/// <see cref="KgsmEventCatalog"/> now. The tests here exist so that neither can quietly come back —
/// the way it comes back is somebody special-casing one event type by name, in a table that already
/// looks like it belongs here.
/// </para>
/// <para>
/// What this does <b>not</b> forbid is naming a type for rendering. The phrase table is exactly that,
/// it is deliberately incomplete, and it is the part that must stay local: how a sentence reads is
/// this surface's business, in a way that "is this field a person's address" is not.
/// </para>
/// </remarks>
public sealed class EventCatalogAdoptionTests
{
    private static HistoryMoment Moment(string type) =>
        new(DateTimeOffset.UnixEpoch, type, "factorio", null, null,
            KgsmEventCatalog.Describe(type).Weight);

    /// <summary>
    /// The whole vocabulary, not a sample: every fact the engine can report reaches the list, and
    /// every step is left out of it. A type silenced by hand — the failure this replaced — fails here.
    /// </summary>
    [Fact]
    public void TheListIsExactlyWhatTheEngineCallsAFact()
    {
        foreach (EventDescriptor descriptor in KgsmEventCatalog.All)
        {
            bool listed = HistoryModule.News([Moment(descriptor.Type)]).Count == 1;

            listed.Should().Be(descriptor.Weight == EventWeight.Fact,
                "{0} is classified {1}", descriptor.Type, descriptor.Weight);
        }
    }

    /// <summary>
    /// A phrase written for an event this surface never lists is dead the moment it is written, and it
    /// reads as a decision somebody made — which is how a second opinion about what counts as news
    /// grows back one line at a time.
    /// </summary>
    [Fact]
    public void NoPhraseIsWrittenForAnEventTheListNeverShows()
    {
        string[] unreachable = [.. HistoryModule.Phrases.Keys
            .Where(type => KgsmEventCatalog.Describe(type).Weight == EventWeight.Phase)
            .OrderBy(type => type, StringComparer.Ordinal)];

        unreachable.Should().BeEmpty(
            "these are steps inside an operation, so /history never renders them — remove the phrase, " +
            "or change the classification in kgsm-lib if the event really is the news");
    }

    /// <summary>
    /// <b>A misspelled key is dead code that fires for nothing</b>, and nothing else would ever notice:
    /// the event still renders, from the engine's own word, exactly as an unnamed one does. Naming a
    /// type the engine does not emit is the same mistake with the same silence.
    /// </summary>
    [Fact]
    public void EveryNamedTypeIsOneTheEngineActuallyEmits()
    {
        string[] unknown = [.. HistoryModule.Phrases.Keys
            .Where(type => !KgsmEventCatalog.Describe(type).Known)
            .OrderBy(type => type, StringComparer.Ordinal)];

        unknown.Should().BeEmpty("these phrases name event types kgsm-lib has never heard of");
    }

    // The other half of the adoption — that no field the engine calls personal or privileged is ever
    // printed — is asserted where the payloads are: ServerHistoryTests.NoFieldTheEngineCallsSensitiveIsEverPrinted
    // feeds a real value for each one. Restating it here against moments carrying no payload would
    // pass without testing anything.
}
