using System.Diagnostics;
using System.Net;

using Discord.Net;

using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The one path out to Discord: what it paces, what it re-tries, and what it refuses.
/// </summary>
/// <remarks>
/// No Discord here. The queue's whole contract is about <i>when</i> a delegate runs and what the
/// caller is told afterwards, so the delegates below record and throw rather than send — which is
/// also what lets a 429 and a 403 be tested at all.
/// </remarks>
public sealed class DiscordSendQueueTests
{
    private static DiscordOptions Fast(Action<DiscordOptions>? tune = null)
    {
        // Real timings would make this suite sleep for minutes. The behaviour under test is the
        // ordering and the arithmetic, neither of which depends on the constants being large.
        var options = new DiscordOptions
        {
            SendQueueMinIntervalMs = 0,
            SendQueueCapacity = 500,
            SendQueueMaxAttempts = 3,
            SendQueueBackoffMs = 100,
            SendQueueMaxBackoffMs = 200,
        };

        tune?.Invoke(options);
        return options;
    }

    private static DiscordSendQueue Queue(DiscordOptions options) =>
        new(Options.Create(options), NullLogger<DiscordSendQueue>.Instance);

    private static HttpException Http(HttpStatusCode code) => new(code, null!, null);

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACallThatWorksReturnsWhatItProduced()
    {
        using DiscordSendQueue queue = Queue(Fast());

        Result<string> result = await queue.SendAsync(
            "post something", SendLane.Announcement, () => Task.FromResult("posted"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("posted");
    }

    /// <summary>
    /// A failed send is a result, never an exception. One guild's dead channel must not unwind the
    /// loop over the others — the caller has to be able to count what it reached.
    /// </summary>
    [Fact]
    public async Task AFailedCallIsReportedRatherThanThrown()
    {
        using DiscordSendQueue queue = Queue(Fast());

        Result result = await queue.SendAsync(
            "post something", SendLane.Announcement,
            () => Task.FromException(Http(HttpStatusCode.Forbidden)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("403");
    }

    // ── ordering ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Announcements go ahead of housekeeping. A crash notice delayed behind fifteen board refreshes
    /// is the news arriving after the incident; a board refresh delayed behind the crash notice says
    /// the same thing a moment later.
    /// </summary>
    [Fact]
    public async Task AnnouncementsDrainAheadOfBackgroundWork()
    {
        // A floor, so everything queues up behind the first call rather than racing the worker.
        using DiscordSendQueue queue = Queue(Fast(o => o.SendQueueMinIntervalMs = 40));

        var order = new List<string>();
        var gate = new TaskCompletionSource();

        // Occupies the worker while the rest are queued, so the lanes are full when it picks again.
        Task blocking = queue.SendAsync("block", SendLane.Background, () => gate.Task);

        List<Task> queued =
        [
            Record("background-1", SendLane.Background),
            Record("background-2", SendLane.Background),
            Record("announcement", SendLane.Announcement),
        ];

        gate.SetResult();
        await blocking;
        await Task.WhenAll(queued);

        order.Should().Equal("announcement", "background-1", "background-2");

        Task Record(string name, SendLane lane) => queue.SendAsync(name, lane, () =>
        {
            lock (order) order.Add(name);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The floor is what keeps a limit from being reached at all — a 429 has already spent the
    /// request that earned it.
    /// </summary>
    [Fact]
    public async Task CallsAreSpacedByTheFloor()
    {
        using DiscordSendQueue queue = Queue(Fast(o => o.SendQueueMinIntervalMs = 60));

        long start = Stopwatch.GetTimestamp();

        await Task.WhenAll(
            queue.SendAsync("a", SendLane.Announcement, () => Task.CompletedTask),
            queue.SendAsync("b", SendLane.Announcement, () => Task.CompletedTask),
            queue.SendAsync("c", SendLane.Announcement, () => Task.CompletedTask));

        // Three calls, two gaps. The first is dispatched immediately — an idle queue pays nothing.
        Stopwatch.GetElapsedTime(start).Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>An idle queue dispatches at once: the floor is a gap between calls, not a delay on one.</summary>
    [Fact]
    public async Task AnIdleQueueDoesNotDelayTheFirstCall()
    {
        using DiscordSendQueue queue = Queue(Fast(o => o.SendQueueMinIntervalMs = 5000));

        long start = Stopwatch.GetTimestamp();
        await queue.SendAsync("a", SendLane.Announcement, () => Task.CompletedTask);

        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    // ── retrying ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARateLimitIsRetriedAndTheCallEventuallySucceeds()
    {
        using DiscordSendQueue queue = Queue(Fast());

        int attempts = 0;
        Result result = await queue.SendAsync("post", SendLane.Announcement, () =>
        {
            attempts++;
            return attempts < 3 ? Task.FromException(Http((HttpStatusCode)429)) : Task.CompletedTask;
        });

        result.IsSuccess.Should().BeTrue();
        attempts.Should().Be(3);
    }

    /// <summary>
    /// A refusal is the answer, not a hiccup. Re-asking spins against a permission that is not
    /// coming back, and for anything that posts it risks a duplicate.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task APermanentRefusalIsNotRetried(HttpStatusCode code)
    {
        using DiscordSendQueue queue = Queue(Fast());

        int attempts = 0;
        Result result = await queue.SendAsync("post", SendLane.Announcement, () =>
        {
            attempts++;
            return Task.FromException(Http(code));
        });

        result.IsFailure.Should().BeTrue();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task RetriesStopAtTheAttemptCap()
    {
        using DiscordSendQueue queue = Queue(Fast(o => o.SendQueueMaxAttempts = 2));

        int attempts = 0;
        Result result = await queue.SendAsync("post", SendLane.Announcement, () =>
        {
            attempts++;
            return Task.FromException(Http(HttpStatusCode.ServiceUnavailable));
        });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("2 attempts");
        attempts.Should().Be(2);
    }

    /// <summary>
    /// Classification is what decides whether a call is worth making again, and the default is "no":
    /// an unrecognised failure retried is a call made twice more for nothing.
    /// </summary>
    [Fact]
    public void OnlyTemporaryFailuresCountAsTransient()
    {
        DiscordSendQueue.IsTransient(Http((HttpStatusCode)429)).Should().BeTrue();
        DiscordSendQueue.IsTransient(Http(HttpStatusCode.BadGateway)).Should().BeTrue();
        DiscordSendQueue.IsTransient(new TimeoutException()).Should().BeTrue();
        DiscordSendQueue.IsTransient(new HttpRequestException()).Should().BeTrue();

        DiscordSendQueue.IsTransient(Http(HttpStatusCode.Forbidden)).Should().BeFalse();
        DiscordSendQueue.IsTransient(Http(HttpStatusCode.NotFound)).Should().BeFalse();
        DiscordSendQueue.IsTransient(new InvalidOperationException()).Should().BeFalse();
    }

    // ── overflow ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A full queue refuses and says so. Dropping silently would make a bot that announces nothing
    /// indistinguishable from a host where nothing happened — and an unbounded one is a memory leak
    /// with a delay on it.
    /// </summary>
    [Fact]
    public async Task AFullLaneRefusesTheCallAndSaysWhy()
    {
        using DiscordSendQueue queue = Queue(Fast(o =>
        {
            o.SendQueueCapacity = 2;
            o.SendQueueMinIntervalMs = 0;
        }));

        var gate = new TaskCompletionSource();

        // Holds the worker so nothing drains while the lane is filled.
        Task blocking = queue.SendAsync("block", SendLane.Announcement, () => gate.Task);
        await WaitUntil(() => queue.Depth.Announcements == 0);

        Task first = queue.SendAsync("first", SendLane.Announcement, () => Task.CompletedTask);
        Task second = queue.SendAsync("second", SendLane.Announcement, () => Task.CompletedTask);

        Result overflow = await queue.SendAsync(
            "third", SendLane.Announcement, () => Task.CompletedTask);

        overflow.IsFailure.Should().BeTrue();
        overflow.Error.Should().Contain("full");

        gate.SetResult();
        await Task.WhenAll(blocking, first, second);
    }

    /// <summary>
    /// The lanes are capped separately, so a backlog of housekeeping cannot crowd out the
    /// announcement somebody is waiting to read.
    /// </summary>
    [Fact]
    public async Task AFullBackgroundLaneDoesNotBlockAnAnnouncement()
    {
        using DiscordSendQueue queue = Queue(Fast(o => o.SendQueueCapacity = 1));

        var gate = new TaskCompletionSource();

        Task blocking = queue.SendAsync("block", SendLane.Background, () => gate.Task);
        await WaitUntil(() => queue.Depth.Background == 0);

        Task filling = queue.SendAsync("fills the lane", SendLane.Background, () => Task.CompletedTask);

        Result refused = await queue.SendAsync(
            "over the cap", SendLane.Background, () => Task.CompletedTask);
        refused.IsFailure.Should().BeTrue();

        // The other lane is untouched by that.
        Task announcement = queue.SendAsync(
            "still accepted", SendLane.Announcement, () => Task.CompletedTask);
        queue.Depth.Announcements.Should().Be(1);

        gate.SetResult();
        await Task.WhenAll(blocking, filling, announcement);
    }

    // ── shutdown ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A caller awaiting a call that will now never be made has to be told, or shutdown waits on a
    /// task nothing will ever complete.
    /// </summary>
    [Fact]
    public async Task ShutdownSettlesEverythingStillWaiting()
    {
        DiscordSendQueue queue = Queue(Fast(o => o.SendQueueMinIntervalMs = 0));

        var gate = new TaskCompletionSource();
        Task blocking = queue.SendAsync("block", SendLane.Background, () => gate.Task);
        await WaitUntil(() => queue.Depth.Background == 0);

        Task<Result> queued = queue.SendAsync("never sent", SendLane.Announcement, () => Task.CompletedTask);

        gate.SetResult();
        queue.Dispose();

        Result result = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("shutting down");

        await blocking;
    }

    [Fact]
    public async Task ACallArrivingAfterShutdownIsRefusedRatherThanQueuedForever()
    {
        DiscordSendQueue queue = Queue(Fast());
        queue.Dispose();

        Result result = await queue
            .SendAsync("too late", SendLane.Announcement, () => Task.CompletedTask)
            .WaitAsync(TimeSpan.FromSeconds(5));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("shutting down");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);

        condition().Should().BeTrue("the queue should have reached the expected state");
    }
}
