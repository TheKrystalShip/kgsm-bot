using System.Reflection;

using FluentAssertions;

using KGSM.Bot.Discord;
using KGSM.Bot.Infrastructure.Configuration;

using Xunit;

namespace KGSM.Bot.Core.Tests.Discord;

/// <summary>
/// The status line's announcement switches against the toggles that actually exist.
/// </summary>
/// <remarks>
/// A switch missing from this list does not look broken anywhere. The Control Panel renders what the
/// line carries, so the row simply never appears and an operator concludes the bot cannot be told to
/// stop announcing that thing — while it goes on announcing it. Reflecting over the options type is
/// what makes adding a toggle and forgetting the row a build failure instead.
/// </remarks>
public class StatusSocketSwitchesTests
{
    private static IEnumerable<PropertyInfo> Toggles() =>
        typeof(AnnouncementOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool));

    /// <summary>
    /// One row per declared toggle, and no row for anything else. The count is asserted alongside the
    /// names so a duplicated key cannot pass by covering for a missing one.
    /// </summary>
    [Fact]
    public void EveryAnnouncementToggleHasARowOnTheStatusLine()
    {
        List<BotSwitch> published = StatusSocketServer.Switches(new AnnouncementOptions());

        published.Should().HaveCount(Toggles().Count());
        published.Select(s => s.Key).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Each row reports the state the bot would actually act on, rather than a second copy of the
    /// defaults. A row that is right about the key and wrong about the value is worse than no row.
    /// </summary>
    [Fact]
    public void EachRowCarriesTheStateTheBotWouldActOn()
    {
        var allOn = new AnnouncementOptions();
        foreach (PropertyInfo toggle in Toggles())
            toggle.SetValue(allOn, true);

        StatusSocketServer.Switches(allOn).Should().OnlyContain(s => s.Enabled);

        var allOff = new AnnouncementOptions();
        foreach (PropertyInfo toggle in Toggles())
            toggle.SetValue(allOff, false);

        StatusSocketServer.Switches(allOff).Should().OnlyContain(s => !s.Enabled);
    }

    /// <summary>
    /// "Update available" specifically: the engine emits it, the bot announces it, and it had no row
    /// here — the shape of omission the two tests above exist to catch.
    /// </summary>
    [Fact]
    public void UpdateAvailableIsOnTheLine()
    {
        StatusSocketServer.Switches(new AnnouncementOptions { UpdateAvailable = true })
            .Should().ContainSingle(s => s.Key == "announceUpdateAvailable" && s.Enabled);
    }
}
