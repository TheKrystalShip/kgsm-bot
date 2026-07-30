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
/// the socket read loop survives. The bot's <see cref="KgsmServerEventHandler"/> never subscribes a
/// <c>RegisterRawHandler</c> (which would see every envelope, including unhandled types, and could
/// throw on a payload it didn't expect), so it never even sees blueprint events or any other new
/// kgsm event type — they fall through the engine read loop untouched. These tests pin that
/// invariant: a future refactor that introduced a raw handler in the bot would force a reconsidered
/// review of unknown-event tolerance here, rather than silently inheriting it from kgsm-lib.
/// </summary>
/// <remarks>
/// The blueprint events added by Phase 2 of <c>blueprint-editor-plan.md</c>
/// (<c>blueprint_created</c>/<c>blueprint_updated</c>/<c>blueprint_removed</c>) are the immediate
/// motivation — they are registered in kgsm-lib's <c>_eventTypeMapping</c>, so they deserialise to
/// their typed <c>Blueprint*Data</c> classes, but no bot handler subscribes to them, so they take the
/// "No handler registered" path and are silently ignored. Verified end-to-end through the
/// bot-wiring seam: <see cref="KgsmServerEventHandler.Initialize"/> registers only its 4 lifecycle
/// handlers and zero raw handlers, which is exactly what the kgsm-lib dispatch contract needs it to
/// be for an unhandled type to degrade cleanly.
/// </remarks>
public class KgsmServerEventHandlerUnknownEventTests
{
    [Fact]
    public void Initialize_RegistersExactlyTheFourLifecycleHandlers_AndNoRawHandler()
    {
        // Capture every RegisterHandler<T> / RegisterRawHandler call against the IEventService the
        // bot reaches through its IKgsmClient.Events seam. The fake returns Substituted tasks so the
        // bot's synchronous Initialize path has nothing to await.
        var events = Substitute.For<IEventService>();
        var kgsmClient = Substitute.For<IKgsmClient>();
        kgsmClient.Events.Returns(events);

        var handler = new KgsmServerEventHandler(kgsmClient, NullLogger<KgsmServerEventHandler>.Instance);
        handler.Initialize();

        // Exactly the four instance lifecycle handlers the bot surfaces to Discord (installed/started/
        // stopped/uninstalled). Nothing else — no blueprint, no port/firewall, no raw handler.
        events.Received(1).RegisterHandler<InstanceInstalledData>(Arg.Any<Func<InstanceInstalledData, Task>>());
        events.Received(1).RegisterHandler<InstanceStartedData>(Arg.Any<Func<InstanceStartedData, Task>>());
        events.Received(1).RegisterHandler<InstanceStoppedData>(Arg.Any<Func<InstanceStoppedData, Task>>());
        events.Received(1).RegisterHandler<InstanceUninstalledData>(Arg.Any<Func<InstanceUninstalledData, Task>>());

        // The structural guarantee that makes unknown-event tolerance kgsm-lib's concern, not the
        // bot's: a raw handler would run on every envelope regardless of type, including blueprint_*,
        // and would have to be written to tolerate an unrecognised payload. The bot registers none,
        // so it never sees envelopes it didn't ask for.
        events.DidNotReceive().RegisterRawHandler(Arg.Any<Func<EventWrapper, Task>>());

        // Count the total RegisterHandler<T> invocations across all four types, as a single-number
        // regression lock against a future addition (e.g. a fifth lifecycle handler) that forgot to
        // come with the matching unknown-event review. The four above are the explicit-name checks;
        // this is the cap.
        var registryCalls = events.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IEventService.RegisterHandler))
            .ToList();
        registryCalls.Should().HaveCount(4, "the bot wires exactly four typed lifecycle handlers");
    }
}