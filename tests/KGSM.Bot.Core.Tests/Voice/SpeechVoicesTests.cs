using System.Reflection;

using FluentAssertions;

using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;


using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// The voice list exists twice and must not drift. An attribute argument has to be a compile-time
/// constant, so the leaf descriptor carries its own literal copy of what
/// <see cref="SpeechVoices.Preferred"/> holds — the Control Panel's dropdown and the order the bot
/// suggests voices in are the same list, and a voice added to one and not the other is a surface
/// offering something another surface has never heard of.
/// </summary>
public class SpeechVoicesTests
{
    /// <summary>
    /// The values the leaf field declares, read by name rather than by type.
    /// </summary>
    /// <remarks>
    /// The attribute is source-included, so the same type name is compiled into every assembly that
    /// uses it and naming it here is ambiguous. What is being asserted is the shipped descriptor's
    /// content, which the name is enough to reach.
    /// </remarks>
    private static string[] Declared()
    {
        Attribute field = typeof(VoiceOptions)
            .GetProperty(nameof(VoiceOptions.SpeechVoice))!
            .GetCustomAttributes()
            .Single(a => a.GetType().Name == "LeafFieldAttribute");

        return (string[])field.GetType().GetProperty("Values")!.GetValue(field)!;
    }

    [Fact]
    public void TheDescriptorOffersExactlyTheVoicesInThePreferredOrder() =>
        Declared().Should().Equal(SpeechVoices.Preferred);

    [Fact]
    public void TheDefaultIsOneOfThem()
    {
        // A default outside its own list renders as a dropdown with nothing selected, which reads as
        // unconfigured rather than as the voice it is actually speaking in.
        string @default = new VoiceOptions().SpeechVoice;
        SpeechVoices.Preferred.Should().Contain(@default);
    }

    [Fact]
    public void EveryVoiceIsNamedOnce() =>
        SpeechVoices.Preferred.Should().OnlyHaveUniqueItems();

    [Fact]
    public void BritishComesFirst()
    {
        // Not cosmetic: the list is what the panel shows in order and what autocomplete suggests
        // before anything is typed, so the first entries are the ones most people will ever try.
        SpeechVoices.Preferred.Take(8).Should().OnlyContain(v => v.StartsWith('b'));
    }
}
