using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.Core;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// What each engine event turns into on its way to a channel. The reduction happens where the
/// payload's type still says what its fields mean, so this is the seam that decides whether a
/// channel reads "crashed — exit 0, attempt 1" or nothing at all.
/// </summary>
public class KgsmServerEventHandlerAnnouncementTests
{
    /// <summary>
    /// Drives the bot's own subscription for <typeparamref name="T"/> with a payload and returns
    /// the announcements it produced. Reaches through the same seam the journal reader does — the
    /// handler the bot registered with kgsm-lib — rather than calling anything private.
    /// </summary>
    private static async Task<List<ServerAnnouncement>> DispatchAsync<T>(T payload)
        where T : KgsmEventDataBase
    {
        var events = Substitute.For<IEventService>();
        var kgsmClient = Substitute.For<IKgsmClient>();
        kgsmClient.Events.Returns(events);

        var handler = new KgsmServerEventHandler(kgsmClient, Labels(), NullLogger<KgsmServerEventHandler>.Instance);

        var announced = new List<ServerAnnouncement>();
        handler.RegisterAnnouncementHandler(a => { announced.Add(a); return Task.CompletedTask; });
        handler.Initialize();

        ICall subscription = events.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IEventService.RegisterHandler)
                      && c.GetMethodInfo().GetGenericArguments().Single() == typeof(T));

        await ((Func<T, Task>)subscription.GetArguments().Single()!)(payload);
        return announced;
    }

    /// <summary>
    /// The supervisor emits one crash event per restart attempt, so a server in a restart loop
    /// produces a run of them seconds apart — measured on this host, four crashes produced eleven
    /// events. The first attempt is the news; the rest would turn a channel into a stack trace, and
    /// the outcome arrives separately as the give-up event.
    /// </summary>
    [Fact]
    public async Task Crash_IsAnnouncedOnTheFirstAttemptOnly()
    {
        var first = await DispatchAsync(new InstanceCrashedData
        {
            InstanceName = "factorio",
            ExitCode = "0",
            Restarts = "1",
        });

        first.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = AnnouncementKind.Crashed,
            InstanceName = "factorio",
            Detail = "exit 0, attempt 1",
        });

        var retry = await DispatchAsync(new InstanceCrashedData
        {
            InstanceName = "factorio",
            ExitCode = "0",
            Restarts = "4",
        });

        retry.Should().BeEmpty("the crash was already announced on attempt 1");
    }

    /// <summary>
    /// An unreadable restart count is announced rather than dropped: silence about a crash is the
    /// worse failure of the two.
    /// </summary>
    [Fact]
    public async Task Crash_WithAnUnreadableRestartCount_IsStillAnnounced()
    {
        var announced = await DispatchAsync(new InstanceCrashedData
        {
            InstanceName = "terraria",
            ExitCode = "unknown",
            Restarts = "",
        });

        announced.Should().ContainSingle().Which.Detail.Should().Be("exit unknown");
    }

    /// <summary>The give-up event is always announced — it is the one that needs somebody to look.</summary>
    [Fact]
    public async Task Failure_IsAnnouncedWithItsRestartCount()
    {
        var announced = await DispatchAsync(new InstanceFailedData
        {
            InstanceName = "valheim",
            ExitCode = "137",
            Restarts = "5",
        });

        announced.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = AnnouncementKind.Failed,
            InstanceName = "valheim",
            Detail = "exit 137, after 5",
        });
    }

    [Fact]
    public async Task Update_NamesTheVersionsItMovedBetween()
    {
        var announced = await DispatchAsync(new InstanceVersionUpdatedData
        {
            InstanceName = "factorio",
            OldVersion = "1.1.109",
            NewVersion = "2.0.14",
        });

        announced.Should().ContainSingle().Which.Detail.Should().Be("1.1.109 → 2.0.14");
    }

    /// <summary>
    /// A version the event did not carry is left out rather than rendered as a blank or an
    /// "unknown" — the sentence gets shorter, it does not get padded.
    /// </summary>
    [Fact]
    public async Task Update_WithOnlyTheNewVersion_NamesJustThat()
    {
        var announced = await DispatchAsync(new InstanceVersionUpdatedData
        {
            InstanceName = "factorio",
            OldVersion = "",
            NewVersion = "2.0.14",
        });

        announced.Should().ContainSingle().Which.Detail.Should().Be("2.0.14");
    }

    /// <summary>
    /// Player identity is nullable by contract: a game that reports only a stable id gets the id.
    /// Neither field is invented to fill the other's place.
    /// </summary>
    [Fact]
    public async Task PlayerJoin_FallsBackToTheIdWhenThereIsNoName()
    {
        var named = await DispatchAsync(new InstancePlayerJoinedData
        {
            InstanceName = "minecraft",
            PlayerName = "someone",
            PlayerId = "76561198000000000",
        });
        named.Should().ContainSingle().Which.Detail.Should().Be("someone");

        var idOnly = await DispatchAsync(new InstancePlayerJoinedData
        {
            InstanceName = "minecraft",
            PlayerName = null,
            PlayerId = "76561198000000000",
        });
        idOnly.Should().ContainSingle().Which.Detail.Should().Be("76561198000000000");
    }

    /// <summary>
    /// The actor travels verbatim. It is not always a person — the supervisor acting on its own
    /// emits <c>system:watchdog</c> — and the bot neither re-derives it nor drops it.
    /// </summary>
    [Fact]
    public async Task TheActorIsCarriedThroughUntouched()
    {
        var announced = await DispatchAsync(new InstanceStartedData
        {
            InstanceName = "factorio",
            Actor = "discord:someone",
        });

        announced.Should().ContainSingle().Which.Actor.Should().Be("discord:someone");
    }

    /// <summary>An event that declared no actor credits nobody, rather than crediting a guess.</summary>
    [Fact]
    public async Task AnAbsentActorIsLeftAbsent()
    {
        var announced = await DispatchAsync(new InstanceStoppedData
        {
            InstanceName = "factorio",
            Actor = null,
        });

        announced.Should().ContainSingle().Which.Actor.Should().BeNull();
    }

    /// <summary>
    /// Install announces after the channel exists, uninstall announces before it is taken away.
    /// Either ordering reversed sends the message to a channel that is not there yet, or is already
    /// gone.
    /// </summary>
    [Fact]
    public async Task InstallAnnouncesAfterItsChannelExists_UninstallBeforeItGoesAway()
    {
        var events = Substitute.For<IEventService>();
        var kgsmClient = Substitute.For<IKgsmClient>();
        kgsmClient.Events.Returns(events);

        var handler = new KgsmServerEventHandler(kgsmClient, Labels(), NullLogger<KgsmServerEventHandler>.Instance);

        var order = new List<string>();
        handler.RegisterAnnouncementHandler(a => { order.Add($"announce:{a.Kind}"); return Task.CompletedTask; });
        handler.RegisterInstanceInstalledHandler((_, _) => { order.Add("channel:create"); return Task.CompletedTask; });
        handler.RegisterInstanceUninstalledHandler(_ => { order.Add("channel:remove"); return Task.CompletedTask; });
        handler.Initialize();

        async Task Dispatch<T>(T payload) where T : KgsmEventDataBase
        {
            ICall subscription = events.ReceivedCalls()
                .Single(c => c.GetMethodInfo().Name == nameof(IEventService.RegisterHandler)
                          && c.GetMethodInfo().GetGenericArguments().Single() == typeof(T));
            await ((Func<T, Task>)subscription.GetArguments().Single()!)(payload);
        }

        await Dispatch(new InstanceInstalledData { InstanceName = "factorio", Blueprint = "factorio" });
        await Dispatch(new InstanceUninstalledData { InstanceName = "factorio" });

        order.Should().Equal(
            "channel:create",
            $"announce:{AnnouncementKind.Installed}",
            $"announce:{AnnouncementKind.Uninstalled}",
            "channel:remove");
    }

    /// <summary>The blueprint a server was installed from is worth naming beside it.</summary>
    [Fact]
    public async Task Install_NamesTheBlueprint()
    {
        var announced = await DispatchAsync(new InstanceInstalledData
        {
            InstanceName = "factorio-test",
            Blueprint = "factorio",
        });

        announced.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = AnnouncementKind.Installed,
            InstanceName = "factorio-test",
            Detail = "factorio",
        });
    }

    /// <summary>
    /// Labels that answer with whatever they are given, which is what a server carrying no label of
    /// its own reads as. A test about a display name says so by supplying one.
    /// </summary>
    private static IServerLabels Labels(string? displayName = null)
    {
        var labels = Substitute.For<IServerLabels>();
        labels.LabelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(displayName ?? call.Arg<string>()));
        return labels;
    }

    /// <summary>
    /// <b>An announcement names the server the way a person does.</b> The event carries the id —
    /// which is what everything downstream keys on and what nobody calls it — so the label is read
    /// off the inventory as the announcement is built, and the id it belongs to travels with it.
    /// </summary>
    [Fact]
    public async Task Announcement_CarriesTheDisplayNameBesideTheId()
    {
        var events = Substitute.For<IEventService>();
        var kgsmClient = Substitute.For<IKgsmClient>();
        kgsmClient.Events.Returns(events);

        var handler = new KgsmServerEventHandler(
            kgsmClient, Labels("My Factorio"), NullLogger<KgsmServerEventHandler>.Instance);

        var announced = new List<ServerAnnouncement>();
        handler.RegisterAnnouncementHandler(a => { announced.Add(a); return Task.CompletedTask; });
        handler.Initialize();

        ICall subscription = events.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IEventService.RegisterHandler)
                      && c.GetMethodInfo().GetGenericArguments().Single() == typeof(InstanceStartedData));

        await ((Func<InstanceStartedData, Task>)subscription.GetArguments().Single()!)(
            new InstanceStartedData { InstanceName = "factorio-42" });

        ServerAnnouncement only = announced.Should().ContainSingle().Subject;
        only.InstanceName.Should().Be("factorio-42");
        only.Label.Should().Be("My Factorio");
    }

    /// <summary>
    /// <b>A rename is not fleet news.</b> Nothing about the server changed — it is up or down exactly
    /// as it was — so the channel hears nothing, and what the event exists for is telling the
    /// surfaces holding the old label to drop it.
    /// </summary>
    [Fact]
    public async Task Rename_RefreshesLabelsAndAnnouncesNothing()
    {
        var events = Substitute.For<IEventService>();
        var kgsmClient = Substitute.For<IKgsmClient>();
        kgsmClient.Events.Returns(events);

        var handler = new KgsmServerEventHandler(
            kgsmClient, Labels(), NullLogger<KgsmServerEventHandler>.Instance);

        var announced = new List<ServerAnnouncement>();
        var renamed = new List<(string Id, string DisplayName)>();
        handler.RegisterAnnouncementHandler(a => { announced.Add(a); return Task.CompletedTask; });
        handler.RegisterInstanceRenamedHandler((id, label) => { renamed.Add((id, label)); return Task.CompletedTask; });
        handler.Initialize();

        ICall subscription = events.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IEventService.RegisterHandler)
                      && c.GetMethodInfo().GetGenericArguments().Single() == typeof(InstanceDisplayNameChangedData));

        await ((Func<InstanceDisplayNameChangedData, Task>)subscription.GetArguments().Single()!)(
            new InstanceDisplayNameChangedData
            {
                InstanceName = "factorio-42",
                OldDisplayName = "factorio-42",
                NewDisplayName = "Fixed Name",
            });

        renamed.Should().ContainSingle().Which.Should().Be(("factorio-42", "Fixed Name"));
        announced.Should().BeEmpty();
    }
}
