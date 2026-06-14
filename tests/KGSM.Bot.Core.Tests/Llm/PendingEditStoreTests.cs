using FluentAssertions;

using KGSM.Bot.Discord.Llm;

using TheKrystalShip.Kgsm.Assistant;

using Xunit;

namespace KGSM.Bot.Tests.Llm;

/// <summary>
/// The overflow fallback for SetConfig confirmations whose value is too long to encode
/// in a Discord customId. Single-use + TTL keeps it from leaking memory or letting a
/// stale button re-fire.
/// </summary>
public class PendingEditStoreTests
{
    private static PendingConfirmation Edit(string value = "--long-args") =>
        new(ConfirmationKind.SetConfig, "factorio",
            InstanceName: null, ConfigKey: "executable_arguments", ConfigValue: value);

    [Fact]
    public void Stash_ThenTake_ReturnsTheConfirmation()
    {
        var store = new PendingEditStore();
        var id = store.Stash(Edit());

        store.TryTake(id, out var taken).Should().BeTrue();
        taken.Should().BeEquivalentTo(Edit());
    }

    [Fact]
    public void Take_IsSingleUse()
    {
        var store = new PendingEditStore();
        var id = store.Stash(Edit());

        store.TryTake(id, out _).Should().BeTrue();
        store.TryTake(id, out _).Should().BeFalse(); // already taken
    }

    [Fact]
    public void Take_UnknownId_ReturnsFalse()
    {
        new PendingEditStore().TryTake("nope", out _).Should().BeFalse();
    }

    [Fact]
    public void ExpiredEntry_IsNotReturned()
    {
        var now = DateTimeOffset.UnixEpoch;
        var store = new PendingEditStore(TimeSpan.FromMinutes(10), () => now);
        var id = store.Stash(Edit());

        now = now.AddMinutes(11); // advance past the TTL
        store.TryTake(id, out _).Should().BeFalse();
    }

    [Fact]
    public void DistinctStashes_GetDistinctIds()
    {
        var store = new PendingEditStore();
        store.Stash(Edit("a")).Should().NotBe(store.Stash(Edit("b")));
    }
}
