using FluentAssertions;

using KGSM.Bot.Discord.Llm;

using TheKrystalShip.Kgsm.Assistant;

using Xunit;

namespace KGSM.Bot.Tests.Llm;

/// <summary>
/// The Discord-transport encoding for a staged <see cref="PendingConfirmation"/>. Every
/// kind must survive the customId round-trip and stay under Discord's 100-char cap — this
/// is what makes the §3.5 generalised commands clickable on the bot surface in lockstep
/// with the library.
/// </summary>
public class ConfirmationIdsTests
{
    public static IEnumerable<object[]> InstanceTargetedKinds() => new[]
    {
        new object[] { ConfirmationKind.Uninstall },
        new object[] { ConfirmationKind.Start },
        new object[] { ConfirmationKind.Stop },
        new object[] { ConfirmationKind.Restart },
        new object[] { ConfirmationKind.Update },
        new object[] { ConfirmationKind.Backup },
    };

    [Theory]
    [MemberData(nameof(InstanceTargetedKinds))]
    public void RoundTrips_InstanceTargetedKinds(ConfirmationKind kind)
    {
        var original = new PendingConfirmation(kind, "factorio-pvp");

        var id = ConfirmationIds.Confirm(original);
        id.Should().StartWith(ConfirmationIds.ConfirmPrefix);
        (id.Length <= ConfirmationIds.MaxCustomIdLength).Should().BeTrue();

        var data = id[ConfirmationIds.ConfirmPrefix.Length..];
        ConfirmationIds.TryParse(data, out var parsed).Should().BeTrue();
        parsed.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData("myserver")]
    [InlineData(null)]
    public void RoundTrips_Install_WithAndWithoutName(string? name)
    {
        var original = new PendingConfirmation(ConfirmationKind.Install, "valheim", name);

        var id = ConfirmationIds.Confirm(original);
        var data = id[ConfirmationIds.ConfirmPrefix.Length..];

        ConfirmationIds.TryParse(data, out var parsed).Should().BeTrue();
        parsed.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData("")]            // empty
    [InlineData("Z~factorio")]  // unknown code
    [InlineData("S")]           // missing target
    [InlineData("S~")]          // empty target
    public void TryParse_Malformed_ReturnsFalse(string data)
    {
        ConfirmationIds.TryParse(data, out _).Should().BeFalse();
    }
}
