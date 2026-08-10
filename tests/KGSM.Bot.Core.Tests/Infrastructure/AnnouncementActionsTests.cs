using FluentAssertions;

using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Discord;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// An announcement is where somebody is already looking at the moment they care, which is what makes
/// a button on it worth having — and what makes the wrong button on it worth avoiding.
/// </summary>
public sealed class AnnouncementActionsTests
{
    /// <summary>
    /// The supervisor gave up: nothing else is coming, so a human restart is the next step.
    /// </summary>
    [Fact]
    public void OnlyAServerNobodyIsAlreadyFixingOffersARestart()
    {
        AnnouncementActions.OffersRestart(AnnouncementKind.Failed).Should().BeTrue();

        // A crash says the supervisor is restarting it. A button here races the supervisor over the
        // same server, and whoever pressed it is blamed for whichever attempt loses.
        AnnouncementActions.OffersRestart(AnnouncementKind.Crashed).Should().BeFalse();

        foreach (AnnouncementKind kind in Enum.GetValues<AnnouncementKind>())
        {
            if (kind == AnnouncementKind.Failed)
                continue;

            AnnouncementActions.OffersRestart(kind).Should().BeFalse(
                "{0} is not a server left down with nothing coming for it", kind);
        }
    }

    [Fact]
    public void BothCrashKindsOpenAThreadAndNothingElseDoes()
    {
        AnnouncementActions.OpensThread(AnnouncementKind.Crashed).Should().BeTrue();
        AnnouncementActions.OpensThread(AnnouncementKind.Failed).Should().BeTrue();

        foreach (AnnouncementKind kind in Enum.GetValues<AnnouncementKind>())
        {
            if (kind is AnnouncementKind.Crashed or AnnouncementKind.Failed)
                continue;

            AnnouncementActions.OpensThread(kind).Should().BeFalse(
                "a thread per {0} would open one for routine traffic", kind);
        }
    }

    /// <summary>
    /// A truncated id acts on a different server or on none, so a name that does not fit gets no
    /// button at all — the announcement is still posted, which is the part that matters.
    /// </summary>
    [Fact]
    public void ANameTooLongForAButtonIsRefusedRatherThanTruncated()
    {
        ServerActionIds.Fits("factorio").Should().BeTrue();
        ServerActionIds.Fits("").Should().BeFalse();

        int room = ServerActionIds.MaxCustomIdLength - ServerActionIds.RestartPrefix.Length;
        ServerActionIds.Fits(new string('a', room)).Should().BeTrue();
        ServerActionIds.Fits(new string('a', room + 1)).Should().BeFalse();

        ServerActionIds.Restart("factorio").Should().StartWith(ServerActionIds.RestartPrefix);
    }

    /// <summary>
    /// The bot's own buttons and the assistant's must not share a prefix: the wildcard handler that
    /// matched first would read the other one's payload as its own — a server name redeemed as a
    /// grant, or a grant restarted as a server.
    /// </summary>
    [Fact]
    public void TheBotsOwnButtonsDoNotShareAPrefixWithTheAssistants()
    {
        ServerActionIds.RestartPrefix.Should()
            .NotStartWith(KGSM.Bot.Discord.Commands.AssistantConfirmationIds.ConfirmPrefix);

        KGSM.Bot.Discord.Commands.AssistantConfirmationIds.ConfirmPrefix.Should()
            .NotStartWith(ServerActionIds.RestartPrefix);

        KGSM.Bot.Discord.Commands.AssistantConfirmationIds.Cancel.Should()
            .NotStartWith(ServerActionIds.RestartPrefix);
    }
}
