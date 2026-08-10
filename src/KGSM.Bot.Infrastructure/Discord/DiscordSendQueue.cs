using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

using Discord.Net;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Discord;

/// <inheritdoc cref="IDiscordSendQueue" />
/// <remarks>
/// <para>
/// <b>One worker, so there is one place the rate is decided.</b> Every unprompted call the bot makes
/// passes through a single loop that dispatches at most one at a time and holds a floor between
/// them. Producers hand over work and stop competing: the announcement fan-out, the board's
/// per-guild edits, the registry's channel management and the cleanup deletes are one stream by the
/// time they reach Discord, whatever order they were produced in.
/// </para>
/// <para>
/// <b>The backlog is bounded, and overflow is reported rather than dropped.</b> An unbounded queue in
/// front of a rate limit is a memory leak with a delay on it — the backlog grows for as long as the
/// producer outruns the drain, and every message in it is staler than the last. A full lane refuses
/// the call and says so, so the caller's own accounting shows a guild it did not reach. Silently
/// discarding it would make a bot that announces nothing indistinguishable from a host where nothing
/// happened.
/// </para>
/// <para>
/// <b>A retry is the same call again, so the calls handed here must be repeatable.</b> Nothing is
/// retried that cannot be: a 403 or a 404 is the answer, not a hiccup, and re-asking spins against a
/// permission that is not coming back.
/// </para>
/// </remarks>
public sealed class DiscordSendQueue : IDiscordSendQueue, IDisposable
{
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordSendQueue> _logger;

    private readonly ConcurrentQueue<WorkItem> _announcements = new();
    private readonly ConcurrentQueue<WorkItem> _background = new();

    /// <summary>Counts what is queued. Incremented before the enqueue, so it never reads low.</summary>
    private int _announcementDepth;
    private int _backgroundDepth;

    /// <summary>Released once per queued item; the worker's only wake-up.</summary>
    private readonly SemaphoreSlim _pending = new(0);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;

    /// <summary>
    /// The item the last attempt failed on, held for one more try. A single slot is enough because
    /// there is one worker: it can only ever be re-trying the item it just dequeued, and holding it
    /// here rather than putting it back keeps it ahead of everything behind it in its lane.
    /// </summary>
    private WorkItem? _retrying;

    /// <summary>When the last call was dispatched, on the monotonic clock.</summary>
    private long _lastDispatch;

    /// <summary>How long the queue is currently holding off for, and until when.</summary>
    private TimeSpan _backoff = TimeSpan.Zero;
    private long _backoffUntil;

    public DiscordSendQueue(IOptions<DiscordOptions> options, ILogger<DiscordSendQueue> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Started here rather than from the gateway's READY handler: this is the path everything
        // else sends through, so it has to be draining before the first producer exists. Idle it
        // costs one task parked on a semaphore.
        _worker = Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <inheritdoc />
    public SendQueueDepth Depth => new(
        Volatile.Read(ref _announcementDepth),
        Volatile.Read(ref _backgroundDepth),
        Stopwatch.GetTimestamp() < Volatile.Read(ref _backoffUntil));

    /// <inheritdoc />
    public Task<Result<T>> SendAsync<T>(string what, SendLane lane, Func<Task<T>> call)
    {
        var settled = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var item = new WorkItem
        {
            What = what,
            Lane = lane,
            Invoke = async () => (object?)await call(),
            Succeed = value => settled.TrySetResult(Result<T>.Success((T)value!)),
            Fail = error => settled.TrySetResult(Result<T>.Failure(error)),
        };

        return Enqueue(item)
            ? settled.Task
            : Task.FromResult(Result<T>.Failure(FullMessage(lane)));
    }

    /// <inheritdoc />
    public Task<Result> SendAsync(string what, SendLane lane, Func<Task> call)
    {
        var settled = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        var item = new WorkItem
        {
            What = what,
            Lane = lane,
            Invoke = async () => { await call(); return null; },
            Succeed = _ => settled.TrySetResult(Result.Success()),
            Fail = error => settled.TrySetResult(Result.Failure(error)),
        };

        return Enqueue(item)
            ? settled.Task
            : Task.FromResult(Result.Failure(FullMessage(lane)));
    }

    // ── queueing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the item, or refuses it because its lane is full.
    /// </summary>
    /// <remarks>
    /// The two lanes are counted separately and capped separately, so a background backlog cannot
    /// crowd out an announcement. Reserving the slot before the enqueue is what makes the cap hold
    /// under concurrent producers — checking the count and then adding would let several callers
    /// past the same last slot.
    /// </remarks>
    private bool Enqueue(WorkItem item)
    {
        if (_stopping.IsCancellationRequested)
        {
            item.Fail("the bot is shutting down");
            return true;
        }

        bool urgent = item.Lane == SendLane.Announcement;
        int capacity = _options.SendQueueCapacity;

        int depth = urgent
            ? Interlocked.Increment(ref _announcementDepth)
            : Interlocked.Increment(ref _backgroundDepth);

        if (depth > capacity)
        {
            if (urgent)
                Interlocked.Decrement(ref _announcementDepth);
            else
                Interlocked.Decrement(ref _backgroundDepth);

            _logger.LogWarning(
                "Dropped a Discord call ({What}): the {Lane} queue is full at {Capacity}. " +
                "Discord is not keeping up with what this host is producing.",
                item.What, item.Lane, capacity);
            return false;
        }

        (urgent ? _announcements : _background).Enqueue(item);
        _pending.Release();
        return true;
    }

    private string FullMessage(SendLane lane) =>
        $"the outbound {lane} queue is full ({_options.SendQueueCapacity}) — Discord is not keeping up";

    // ── draining ──────────────────────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Discord send queue: at most one call every {Interval}ms, up to {Capacity} waiting per lane, " +
            "{Attempts} attempts each.",
            _options.SendQueueMinIntervalMs, _options.SendQueueCapacity, _options.SendQueueMaxAttempts);

        while (!cancellationToken.IsCancellationRequested)
        {
            WorkItem item;
            try
            {
                item = await NextAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await DispatchAsync(item, cancellationToken);
        }

        // Nothing else will run these, and a caller awaiting a task that never completes would hold
        // shutdown open. Every one of them is told, in the same shape as any other failure.
        FailBacklog();
    }

    /// <summary>
    /// The next call to make: the one being retried, else the oldest announcement, else the oldest
    /// piece of housekeeping.
    /// </summary>
    private async Task<WorkItem> NextAsync(CancellationToken cancellationToken)
    {
        if (_retrying is WorkItem again)
        {
            _retrying = null;
            return again;
        }

        while (true)
        {
            await _pending.WaitAsync(cancellationToken);

            if (_announcements.TryDequeue(out WorkItem? urgent))
            {
                Interlocked.Decrement(ref _announcementDepth);
                return urgent;
            }

            if (_background.TryDequeue(out WorkItem? routine))
            {
                Interlocked.Decrement(ref _backgroundDepth);
                return routine;
            }

            // The semaphore counts items, so this is unreachable in practice. Looping rather than
            // returning null keeps that assumption from becoming a null the caller has to handle.
        }
    }

    private async Task DispatchAsync(WorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForBackoffAsync(cancellationToken);
            await PaceAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            item.Fail("the bot is shutting down");
            return;
        }

        _lastDispatch = Stopwatch.GetTimestamp();
        item.Attempts++;

        try
        {
            object? value = await item.Invoke();

            // A call that worked is evidence the limit is not being hit any more. Decaying instead of
            // clearing would keep pacing the queue slowly on the strength of one old 429.
            _backoff = TimeSpan.Zero;
            item.Succeed(value);
        }
        catch (Exception e)
        {
            OnFailed(item, e);
        }
    }

    /// <summary>
    /// Decides whether the call gets another go, and how long the queue holds off before it does.
    /// </summary>
    private void OnFailed(WorkItem item, Exception e)
    {
        if (!IsTransient(e))
        {
            _logger.LogWarning(e, "Discord refused a call ({What}); not retrying.", item.What);
            item.Fail(Describe(e));
            return;
        }

        if (item.Attempts >= _options.SendQueueMaxAttempts)
        {
            _logger.LogWarning(e,
                "Gave up on a Discord call ({What}) after {Attempts} attempts.",
                item.What, item.Attempts);
            item.Fail($"{Describe(e)} (after {item.Attempts} attempts)");
            return;
        }

        // The whole queue slows, not just this item. One call spinning against a limit while
        // everything else waits behind it is how a single hot channel takes the rest of the bot
        // down with it — the thing this queue exists to prevent.
        _backoff = _backoff == TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(_options.SendQueueBackoffMs)
            : _backoff * 2;

        TimeSpan cap = TimeSpan.FromMilliseconds(_options.SendQueueMaxBackoffMs);
        if (_backoff > cap)
            _backoff = cap;

        // Jitter, because every producer here is driven by the same events: without it a burst that
        // backed off together comes back together and hits the same limit again.
        TimeSpan wait = _backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        Volatile.Write(ref _backoffUntil, Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency));

        _logger.LogWarning(
            "Discord call failed ({What}, attempt {Attempts}): {Reason}. Holding the queue for {Wait}ms.",
            item.What, item.Attempts, Describe(e), (int)wait.TotalMilliseconds);

        _retrying = item;
    }

    /// <summary>Holds off while the queue is backing off from a rate limit or a server error.</summary>
    private async Task WaitForBackoffAsync(CancellationToken cancellationToken)
    {
        long until = Volatile.Read(ref _backoffUntil);
        TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), until);

        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken);
    }

    /// <summary>
    /// Keeps a floor between calls, so a burst is spread rather than delivered at once.
    /// </summary>
    /// <remarks>
    /// This is the part that stops a limit being reached in the first place, and it is worth more
    /// than the backoff: a 429 already spent the request that earned it. An idle queue dispatches
    /// immediately — the floor is a gap between calls, not a delay on every one.
    /// </remarks>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (_lastDispatch == 0)
            return;

        TimeSpan floor = TimeSpan.FromMilliseconds(_options.SendQueueMinIntervalMs);
        TimeSpan since = Stopwatch.GetElapsedTime(_lastDispatch);

        if (since < floor)
            await Task.Delay(floor - since, cancellationToken);
    }

    private void FailBacklog()
    {
        _retrying?.Fail("the bot is shutting down");
        _retrying = null;

        while (_announcements.TryDequeue(out WorkItem? item))
        {
            Interlocked.Decrement(ref _announcementDepth);
            item.Fail("the bot is shutting down");
        }

        while (_background.TryDequeue(out WorkItem? item))
        {
            Interlocked.Decrement(ref _backgroundDepth);
            item.Fail("the bot is shutting down");
        }
    }

    // ── classifying a failure ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether asking again could plausibly get a different answer.
    /// </summary>
    /// <remarks>
    /// <b>The default is "no".</b> An unrecognised exception retried is a call made two more times
    /// for nothing, and — for anything that posts — possibly a duplicate message. A rate limit, a
    /// server error and a dropped connection are the three that genuinely pass; a refusal, a missing
    /// channel and a malformed request are the answer.
    /// <para>
    /// <see cref="RateLimitedException"/> is matched before <see cref="TimeoutException"/> because it
    /// derives from it, and an arm below its base would never be reached.
    /// </para>
    /// </remarks>
    internal static bool IsTransient(Exception e) => e switch
    {
        RateLimitedException => true,
        HttpException http => http.HttpCode == (HttpStatusCode)429 || (int)http.HttpCode >= 500,
        TimeoutException => true,
        HttpRequestException => true,
        WebSocketException => true,
        SocketException => true,
        IOException => true,
        _ => false,
    };

    /// <summary>
    /// The failure in one line. Discord's own reason where there is one — "Missing Permissions" is
    /// what an operator has to act on, and the exception's own text buries it.
    /// </summary>
    internal static string Describe(Exception e) => e switch
    {
        RateLimitedException => "rate limited",
        HttpException http when !string.IsNullOrWhiteSpace(http.Reason) =>
            $"Discord returned {(int)http.HttpCode}: {http.Reason}",
        HttpException http => $"Discord returned {(int)http.HttpCode}",
        _ => e.Message,
    };

    public void Dispose()
    {
        _stopping.Cancel();

        // Bounded, because Dispose runs on the shutdown path: a worker wedged inside a Discord call
        // that is not observing cancellation must not hold the process open.
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop swallows its own failures; anything here is the cancellation unwinding.
        }

        // Runs whether or not the worker got there — a Wait that timed out left the backlog standing,
        // and the callers waiting on it are the reason this is not left to the worker alone.
        FailBacklog();

        _stopping.Dispose();
        _pending.Dispose();
    }

    /// <summary>
    /// One queued call: what to run, how to settle the caller, and how many times it has been tried.
    /// </summary>
    /// <remarks>
    /// The typed result is boxed through <see cref="object"/> so one queue carries calls of every
    /// return type. Only the two <c>SendAsync</c> overloads create these, and each pairs an
    /// <see cref="Invoke"/> with the <see cref="Succeed"/> that unboxes exactly what it produced, so
    /// the cast cannot be given the wrong type.
    /// </remarks>
    private sealed class WorkItem
    {
        public required string What { get; init; }
        public required SendLane Lane { get; init; }
        public required Func<Task<object?>> Invoke { get; init; }
        public required Action<object?> Succeed { get; init; }
        public required Action<string> Fail { get; init; }
        public int Attempts;
    }
}
