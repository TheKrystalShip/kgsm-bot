using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.Core;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// Locks the bot's tolerance of kgsm event types it does not handle. kgsm-lib's
/// <c>EventService.OnEventReceivedAsync</c> already does the safe thing for an unrecognised or
/// unhandled type: a <c>_eventTypeMapping</c> miss logs "Unknown event type" and returns; a hit
/// whose data type has no registered handler logs "No handler registered" and returns. Either way
/// the journal read loop survives. The bot's <see cref="KgsmServerEventHandler"/> never subscribes a
/// <c>RegisterRawHandler</c> (which would see every envelope, including unhandled types, and could
/// throw on a payload it didn't expect), so it never sees an event type it did not ask for. These
/// tests pin that invariant: a future refactor that introduced a raw handler in the bot would force
/// a reconsidered review of unknown-event tolerance here, rather than silently inheriting it from
/// kgsm-lib.
/// </summary>
/// <remarks>
/// The bot subscribes one typed handler per <see cref="KGSM.Bot.Core.Models.AnnouncementKind"/>,
/// plus the two lifecycle types whose handling is not an announcement. Event types outside that set
/// — blueprint events, ports, UPnP, config changes, the download/deploy brackets — are registered in
/// kgsm-lib's <c>_eventTypeMapping</c>, so they deserialise to their typed classes, but no bot
/// handler subscribes to them and they take the "No handler registered" path. That is the degrade
/// path this file exists to keep honest.
/// </remarks>
public class KgsmServerEventHandlerUnknownEventTests
{
    /// <summary>
    /// The event types the bot subscribes to. One line per subscription, so adding an announcement
    /// without deciding what its event is named fails here rather than in a Discord channel.
    /// </summary>
    private static readonly Type[] SubscribedEventTypes =
    [
        // Lifecycle: these carry a channel to create or retire, on top of announcing.
        typeof(InstanceInstalledData),
        typeof(InstanceUninstalledData),

        // Run state.
        typeof(InstanceStartedData),
        typeof(InstanceReadyData),
        typeof(InstanceStoppedData),
        typeof(InstanceRestartedData),
        typeof(InstanceCrashedData),
        typeof(InstanceFailedData),

        // The engine reports an update by naming the versions it moved between; there is no bare
        // "updated" event on the wire, so there is nothing else here to subscribe to.
        typeof(InstanceVersionUpdatedData),

        typeof(InstanceBackupCreatedData),
        typeof(InstanceBackupRestoredData),

        typeof(InstancePlayerJoinedData),
        typeof(InstancePlayerLeftData),

        typeof(InstancePlayerKickedData),
        typeof(InstancePlayerBannedData),
        typeof(InstancePlayerUnbannedData),
    ];

    [Fact]
    public void Initialize_RegistersOneTypedHandlerPerSubscribedEvent_AndNoRawHandler()
    {
        // Capture every RegisterHandler<T> / RegisterRawHandler call against the IEventService the
        // bot reaches through its IKgsmClient.Events seam. The fake returns Substituted tasks so the
        // bot's synchronous Initialize path has nothing to await.
        var events = Substitute.For<IEventService>();
        var kgsmClient = Substitute.For<IKgsmClient>();
        kgsmClient.Events.Returns(events);

        var handler = new KgsmServerEventHandler(kgsmClient, NullLogger<KgsmServerEventHandler>.Instance);
        handler.Initialize();

        // RegisterHandler<T> is generic, so the type argument is where the subscription actually
        // lives — read it back off each call rather than asserting a count that a wrong-typed
        // subscription would still satisfy.
        var subscribed = events.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IEventService.RegisterHandler))
            .Select(c => c.GetMethodInfo().GetGenericArguments().Single())
            .ToList();

        subscribed.Should().BeEquivalentTo(SubscribedEventTypes,
            "the bot subscribes exactly the event types it announces or keeps channels for");

        subscribed.Should().OnlyHaveUniqueItems(
            "a type subscribed twice announces the same event twice");

        // The structural guarantee that makes unknown-event tolerance kgsm-lib's concern, not the
        // bot's: a raw handler would run on every envelope regardless of type, including the ones
        // above plus every type the bot has no model for, and would have to be written to tolerate
        // an unrecognised payload. The bot registers none.
        events.DidNotReceive().RegisterRawHandler(Arg.Any<Func<EventWrapper, Task>>());
    }
}
