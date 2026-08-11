using System.Text.Json;

using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

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
        Answers(new EventHistoryPage([Entry("instance_started")], null, null, from, true, true));

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
            [Entry("instance_config_changed", new { InstanceName = "factorio", Key = "memory_cap_mb" })],
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
            [Entry("instance_player_joined", new
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
            [Entry("instance_player_joined", new { InstanceName = "factorio", PlayerAddr = "95.49.44.91" })],
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
            [Entry("instance_input_sent", new { InstanceName = "factorio", Command = "op somebody" })],
            null, null, null, false, true));

        HistoryMoment moment = (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single();

        moment.Type.Should().Be("instance_input_sent");
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
            [Entry("instance_ports_opened", new
            {
                InstanceName = "factorio",
                Ports = new[] { new { start = 21025, end = 21025, protocol = "tcp" } },
            })],
            null, null, null, false, true));

        (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single().Detail.Should().BeNull();
    }

    [Fact]
    public async Task AnEventWithNoRecognisedFieldHasNoDetail()
    {
        Answers(new EventHistoryPage(
            [Entry("instance_stopped", new { InstanceName = "factorio" })], null, null, null, false, true));

        (await History().ReadAsync(null, TimeSpan.FromHours(24), 200)).Moments.Single().Detail.Should().BeNull();
    }
}
