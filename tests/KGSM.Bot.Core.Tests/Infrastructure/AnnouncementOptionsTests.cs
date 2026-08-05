using FluentAssertions;

using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// The toggles are the whole point of the announcement surface — an operator switching one off has
/// to mean the channel goes quiet about it. These pin the map between a kind and the switch that
/// decides it.
/// </summary>
public class AnnouncementOptionsTests
{
    /// <summary>
    /// Every kind is answered by a switch of its own, not by the fallback. The fallback exists so a
    /// newly added kind is loud rather than silently dropped; a kind that reaches it in a shipped
    /// build is a knob the Control Panel cannot turn off.
    /// </summary>
    [Fact]
    public void EveryAnnouncementKind_IsDecidedByAnActualSwitch()
    {
        // All switches off. Any kind still reporting enabled fell through to the `_ => true`
        // fallback, which means nothing in the panel controls it.
        var allOff = new AnnouncementOptions
        {
            Started = false,
            Ready = false,
            Stopped = false,
            Restarted = false,
            Crashed = false,
            Failed = false,
            Updated = false,
            Installed = false,
            Uninstalled = false,
            BackupCreated = false,
            BackupRestored = false,
            PlayerJoined = false,
            PlayerLeft = false,
            Moderation = false,
        };

        foreach (AnnouncementKind kind in Enum.GetValues<AnnouncementKind>())
        {
            allOff.IsEnabled(kind).Should().BeFalse(
                "{0} must be controlled by a switch an operator can see, not by the fallback", kind);
        }
    }

    [Fact]
    public void OneSwitchOn_EnablesOnlyThatKind()
    {
        var options = new AnnouncementOptions
        {
            Started = false,
            Ready = false,
            Stopped = false,
            Restarted = false,
            Crashed = true,
            Failed = false,
            Updated = false,
            Installed = false,
            Uninstalled = false,
            BackupCreated = false,
            BackupRestored = false,
            PlayerJoined = false,
            PlayerLeft = false,
            Moderation = false,
        };

        options.IsEnabled(AnnouncementKind.Crashed).Should().BeTrue();
        options.IsEnabled(AnnouncementKind.Failed).Should().BeFalse();
        options.IsEnabled(AnnouncementKind.Started).Should().BeFalse();
    }

    /// <summary>
    /// The three moderation kinds share one switch on purpose: an operator who wants to hear about
    /// bans wants to hear about kicks, and three near-identical rows would be panel noise.
    /// </summary>
    [Theory]
    [InlineData(AnnouncementKind.PlayerKicked)]
    [InlineData(AnnouncementKind.PlayerBanned)]
    [InlineData(AnnouncementKind.PlayerUnbanned)]
    public void ModerationKinds_ShareOneSwitch(AnnouncementKind kind)
    {
        new AnnouncementOptions { Moderation = true }.IsEnabled(kind).Should().BeTrue();
        new AnnouncementOptions { Moderation = false }.IsEnabled(kind).Should().BeFalse();
    }

    /// <summary>
    /// The shipped defaults. The ones that are off fire on their own cadence rather than on an
    /// operator's action, and would double or triple a channel's traffic.
    /// </summary>
    [Fact]
    public void Defaults_LeaveTheSelfFiringKindsOff()
    {
        var defaults = new AnnouncementOptions();

        defaults.IsEnabled(AnnouncementKind.Ready).Should().BeFalse();
        defaults.IsEnabled(AnnouncementKind.PlayerJoined).Should().BeFalse();
        defaults.IsEnabled(AnnouncementKind.PlayerLeft).Should().BeFalse();

        defaults.IsEnabled(AnnouncementKind.Started).Should().BeTrue();
        defaults.IsEnabled(AnnouncementKind.Stopped).Should().BeTrue();
        defaults.IsEnabled(AnnouncementKind.Crashed).Should().BeTrue();
        defaults.IsEnabled(AnnouncementKind.Failed).Should().BeTrue();
    }
}
