using FluentAssertions;

using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Discord;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// The sentence each kind produces. A channel is the only place these are read, and nobody reads
/// them there until something has already gone out — so the wording is pinned here instead.
/// </summary>
public class AnnouncementWordingTests
{
    /// <summary>
    /// Every kind says something written for it. The fallback prints the enum name, which reaches a
    /// channel as "factorio UpdateAvailable" — legible enough to survive review and wrong enough to
    /// be worth failing a build over.
    /// </summary>
    [Fact]
    public void EveryAnnouncementKind_HasASentenceOfItsOwn()
    {
        foreach (AnnouncementKind kind in Enum.GetValues<AnnouncementKind>())
        {
            DiscordNotificationService.VerbFor(kind).Should().NotBe(kind.ToString(),
                "{0} reached the enum-name fallback instead of a written sentence", kind);
        }
    }

    /// <summary>
    /// The two update kinds are different facts and must not read alike: one says a build exists,
    /// the other says this server is running it. Both carry a version pair, so the sentence is the
    /// only thing telling them apart.
    /// </summary>
    [Fact]
    public void TheTwoUpdateKinds_DoNotReadAlike()
    {
        string available = DiscordNotificationService.VerbFor(AnnouncementKind.UpdateAvailable);
        string applied = DiscordNotificationService.VerbFor(AnnouncementKind.Updated);

        available.Should().Be("has an update available");
        applied.Should().Be("was updated");
        available.Should().NotBe(applied);
    }
}
