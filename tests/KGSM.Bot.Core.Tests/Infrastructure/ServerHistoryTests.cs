using System.Text.Json;

using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// Reading the durable record, and the honesty signals that qualify every answer it gives.
/// </summary>
public sealed class ServerHistoryTests
{
    private readonly IEventJournalHistory _journal = Substitute.For<IEventJournalHistory>();

    private ServerHistory History() => new(_journal, NullLogger<ServerHistory>.Instance);

    private void Answers(EventHistoryPage page) =>
        _journal.QueryAsync(Arg.Any<EventHistoryQuery>(), Arg.Any<CancellationToken>()).Returns(page);

    private static EventHistoryEntry Entry(string type, object? data = null) =>
        new("evt_1", DateTimeOffset.UnixEpoch, type, "factorio", null, "discord:heisen", "discord", "hotrod",
            data is null ? null : JsonSerializer.SerializeToElement(data));

    /// <summary>
    /// <b>An unreadable journal is not a quiet host.</b> Both come back with no events, and rendering
    /// them the same tells somebody nothing happened overnight on the strength of a permission error.
    /// </summary>
    [Fact]
    public async Task AnUnreadableJournalIsNotAnEmptyOne()
    {
        Answers(EventHistoryPage.Unreadable);
        (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).JournalReadable.Should().BeFalse();

        Answers(EventHistoryPage.Empty(DateTimeOffset.UnixEpoch));
        HostHistory quiet = await History().ReadAsync(null, TimeSpan.FromHours(24), 200);

        quiet.JournalReadable.Should().BeTrue();
        quiet.Moments.Should().BeEmpty();
    }

    /// <summary>
    /// The reader contracts not to throw, so anything that does is a surprise — and a surprise must
    /// land as "could not read" rather than as an empty history or an unhandled interaction.
    /// </summary>
    [Fact]
    public async Task AThrownReadIsReportedAsUnreadable()
    {
        _journal.QueryAsync(Arg.Any<EventHistoryQuery>(), Arg.Any<CancellationToken>())
            .Returns<EventHistoryPage>(_ => throw new IOException("the journal directory went away"));

        (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).JournalReadable.Should().BeFalse();
    }

    /// <summary>
    /// Both limits travel to the reader unflattened: a window that reaches further back than the
    /// journal keeps, and a scan that stopped at its budget, are different qualifications and a
    /// renderer needs to say each of them.
    /// </summary>
    [Fact]
    public async Task CoverageAndTruncationAreCarriedAcross()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-3);
        Answers(new EventHistoryPage([Entry("server.started")], null, null, from, true, true));

        HostHistory history = await History().ReadAsync(null, TimeSpan.FromDays(30), 200);

        history.CoverageFrom.Should().Be(from);
        history.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task TheWindowAndTheScopeReachTheQuery()
    {
        Answers(EventHistoryPage.Empty(null));

        await History().ReadAsync("factorio", TimeSpan.FromHours(6), 50);

        await _journal.Received(1).QueryAsync(
            Arg.Is<EventHistoryQuery>(q =>
                q.Instance == "factorio"
                && q.Limit == 50
                && q.SinceMs != null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// No instance means the whole host, which the reader spells as an unset filter rather than as an
    /// empty string — an empty one would scope every event to a server called "".
    /// </summary>
    [Fact]
    public async Task NoServerMeansTheWholeHost()
    {
        Answers(EventHistoryPage.Empty(null));

        await History().ReadAsync("   ", TimeSpan.FromHours(6), 50);

        await _journal.Received(1).QueryAsync(
            Arg.Is<EventHistoryQuery>(q => q.Instance == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheMostSpecificFieldOnThePayloadBecomesTheDetail()
    {
        Answers(new EventHistoryPage(
            [Entry("config.changed", new { InstanceName = "factorio", Key = "memory_cap_mb" })],
            null, null, null, false, true));

        HostHistory history = await History().ReadAsync(null, TimeSpan.FromHours(24), 200);

        history.Moments.Single().Detail.Should().Be("memory_cap_mb");
    }

    /// <summary>
    /// <b>A player's network address is never lifted onto a line.</b> It identifies a connection
    /// rather than a person, and the roster refuses to print one for exactly this reason — a generic
    /// field reader that grabbed whatever was there would publish it to the channel instead.
    /// </summary>
    [Fact]
    public async Task APlayerIsNamedByTheirNameAndNeverByTheirAddress()
    {
        Answers(new EventHistoryPage(
            [Entry("player.joined", new
            {
                InstanceName = "factorio",
                PlayerName = "bob",
                PlayerAddr = "95.49.44.91",
                SessionKey = "abc",
            })],
            null, null, null, false, true));

        HistoryMoment moment = (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single();

        moment.Detail.Should().Be("bob");
        moment.Detail.Should().NotContain("95.49");
    }

    /// <summary>
    /// A join with no name is counted and not labelled, the same rule the roster follows: the address
    /// is there on the payload and is still not what identifies somebody.
    /// </summary>
    [Fact]
    public async Task AnUnnamedPlayerGetsNoDetailRatherThanAnAddress()
    {
        Answers(new EventHistoryPage(
            [Entry("player.joined", new { InstanceName = "factorio", PlayerAddr = "95.49.44.91" })],
            null, null, null, false, true));

        (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single().Detail.Should().BeNull();
    }

    /// <summary>
    /// Console input is what somebody typed at a game server, and this surface answers a viewer. The
    /// event itself is shown — that it happened is not hidden — but the command is not lifted out of
    /// the payload onto the line.
    /// </summary>
    [Fact]
    public async Task ConsoleInputIsRecordedWithoutQuotingWhatWasTyped()
    {
        Answers(new EventHistoryPage(
            [Entry("console.input.sent", new { InstanceName = "factorio", Command = "op somebody" })],
            null, null, null, false, true));

        HistoryMoment moment = (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single();

        moment.Type.Should().Be("console.input.sent");
        moment.Detail.Should().BeNull();
    }

    /// <summary>
    /// A structured field is left to the renderer that understands it. Ports have one on
    /// <c>/connect</c>, and a generic reader would put raw JSON in a sentence.
    /// </summary>
    [Fact]
    public async Task AStructuredFieldIsNotFlattenedIntoTheLine()
    {
        Answers(new EventHistoryPage(
            [Entry("network.ports.opened", new
            {
                InstanceName = "factorio",
                Ports = new[] { new { start = 21025, end = 21025, protocol = "tcp" } },
            })],
            null, null, null, false, true));

        (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single().Detail.Should().BeNull();
    }

    /// <summary>
    /// Who a kick or a ban named is not printed, because <em>the event does not say what kind of
    /// identity it is</em> — the game's blueprint does, and on some games it is an address. The
    /// catalog calls that conditional and instructs a consumer that cannot resolve it to treat it as
    /// personal; this one cannot, so it does.
    /// </summary>
    [Fact]
    public async Task AModerationTargetIsNotPrintedBecauseNothingHereKnowsWhatItIs()
    {
        Answers(new EventHistoryPage(
            [Entry("player.banned", new
            {
                InstanceName = "factorio",
                Target = "95.49.44.91",
                Command = "/ban 95.49.44.91",
            })],
            null, null, null, false, true));

        HistoryMoment moment = (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single();

        moment.Type.Should().Be("player.banned");
        moment.Detail.Should().BeNull();
    }

    /// <summary>
    /// <b>The property behind every exclusion above, asserted over the whole vocabulary rather than
    /// event by event.</b> This surface holds no list of what is sensitive — it prints what the engine
    /// calls public — so a field classified anything else, on any event, must not reach a line. A field
    /// reclassified upstream is covered here on the day the pin moves, with no edit.
    /// </summary>
    [Fact]
    public async Task NoFieldTheEngineCallsSensitiveIsEverPrinted()
    {
        foreach (EventDescriptor descriptor in KgsmEventCatalog.All)
        {
            foreach (EventField field in descriptor.Fields.Where(f => f.Sensitivity != FieldSensitivity.Public))
            {
                var payload = new Dictionary<string, object>
                {
                    ["InstanceName"] = "factorio",
                    [field.Name] = "the-sensitive-value",
                };

                Answers(new EventHistoryPage(
                    [Entry(descriptor.Type, payload)], null, null, null, false, true));

                HistoryMoment moment =
                    (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single();

                moment.Detail.Should().NotBe("the-sensitive-value",
                    "{0}.{1} is {2}", descriptor.Type, field.Name, field.Sensitivity);
            }
        }
    }

    /// <summary>
    /// The engine's own weight rides on the moment rather than being re-derived by whoever renders it.
    /// An install's brackets and the install itself are both carried; which of them a surface shows is
    /// that surface's decision.
    /// </summary>
    [Fact]
    public async Task WhetherAnEventIsTheNewsComesFromTheEngineAndIsCarried()
    {
        Answers(new EventHistoryPage(
            [Entry("server.installed"), Entry("server.deploy.started"), Entry("server.some_future_thing")],
            null, null, null, false, true));

        IReadOnlyList<HistoryMoment> moments =
            (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments;

        moments[0].Weight.Should().Be(EventWeight.Fact);
        moments[1].Weight.Should().Be(EventWeight.Phase);

        // A type this build has never heard of is news until somebody says otherwise — the cautious
        // answer is the one that still shows up.
        moments[2].Weight.Should().Be(EventWeight.Fact);
    }

    [Fact]
    public async Task AnEventWithNoRecognisedFieldHasNoDetail()
    {
        Answers(new EventHistoryPage(
            [Entry("server.stopped", new { InstanceName = "factorio" })], null, null, null, false, true));

        (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single().Detail.Should().BeNull();
    }
}
