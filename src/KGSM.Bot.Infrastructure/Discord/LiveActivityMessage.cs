using System.Text;

using Discord;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// One message that shows what the assistant is doing, kept current by editing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One message, not one per step.</b> A turn consults four or five tools, and a message each
/// would be five posts against a rate-limit budget shared with every announcement on the host — the
/// same arithmetic that makes run state in a channel name unbuildable. Editing one message spends a
/// different, far more generous bucket, and it also reads better: the steps stay together as one
/// account of one turn instead of scrolling apart.
/// </para>
/// <para>
/// <b>Edits are coalesced against a floor.</b> Tool results can land within a second of each other,
/// and spending an edit on each would burst exactly when the turn is busiest. Whatever arrives inside
/// the window is published together, and the final state is always published — a coalescing window
/// that can swallow the last update is a message that stops mid-turn and stays that way.
/// </para>
/// <para>
/// <b>It describes, never quotes.</b> Every line comes from an <see cref="AssistantActivity"/>, which
/// carries what was consulted and never what it returned.
/// </para>
/// </remarks>
public sealed class LiveActivityMessage : IProgress<AssistantActivity>, IAsyncDisposable
{
    /// <summary>The floor between edits. Anything arriving inside it is published with the next one.</summary>
    private static readonly TimeSpan EditFloor = TimeSpan.FromSeconds(2);

    /// <summary>Discord hard-caps a single message at 2000 characters.</summary>
    private const int DiscordMessageLimit = 2000;

    /// <summary>
    /// How many steps are listed. A turn that runs away is still one readable message rather than a
    /// wall; the count of what is not shown is printed instead, so a truncated list never reads as a
    /// complete one.
    /// </summary>
    private const int MaxRows = 12;

    private readonly IMessageChannel _channel;
    private readonly IDiscordSendQueue _queue;
    private readonly ILogger _logger;
    private readonly string _headline;
    private readonly string _what;

    private readonly Lock _gate = new();
    private readonly List<AssistantActivity> _steps = [];

    private IUserMessage? _message;
    private DateTimeOffset _lastEdit = DateTimeOffset.MinValue;
    private Task _pending = Task.CompletedTask;
    private bool _finished;

    /// <param name="channel">Where the message lives — a thread, or the channel somebody asked in.</param>
    /// <param name="queue">The paced path out to Discord; every post and edit goes through it.</param>
    /// <param name="headline">The first line, naming what is being worked on.</param>
    /// <param name="what">This message in words, for the queue's log.</param>
    /// <param name="logger">The logger to use.</param>
    public LiveActivityMessage(
        IMessageChannel channel, IDiscordSendQueue queue, string headline, string what, ILogger logger)
    {
        _channel = channel;
        _queue = queue;
        _headline = headline;
        _what = what;
        _logger = logger;
    }

    /// <summary>
    /// Posts the opening state, so the place somebody is looking says something is happening before
    /// the first tool has finished.
    /// </summary>
    /// <remarks>
    /// A failure to post leaves this inert rather than throwing: the work it narrates is the point,
    /// and a turn must not fail because the commentary on it could not be written.
    /// </remarks>
    public async Task StartAsync()
    {
        try
        {
            var posted = await _queue.SendAsync(
                _what,
                SendLane.Announcement,
                async () => (IUserMessage)await _channel.SendMessageAsync(
                    Render(), allowedMentions: AllowedMentions.None));

            if (posted is null || posted.IsFailure)
            {
                _logger.LogDebug(
                    "Could not post the live activity message ({What}): {Reason}. The work itself is unaffected.",
                    _what, posted?.Error ?? "the send queue answered with nothing");
                return;
            }

            _message = posted.Value;
            _lastEdit = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            // Narrating the work must never cost the work. This is called from inside the turn's own
            // try, so an exception here would abort the investigation it was meant to describe — and
            // the findings are what somebody is waiting for. A message that never appears leaves this
            // inert: every later edit checks for it.
            _logger.LogDebug(ex,
                "Could not post the live activity message ({What}); the work itself is unaffected.", _what);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Called from the stream reader as frames arrive. It records and returns: an edit is dispatched
    /// without being awaited, because a renderer that blocked here would slow the turn it is
    /// describing.
    /// </remarks>
    public void Report(AssistantActivity value)
    {
        if (value is null)
            return;

        lock (_gate)
        {
            if (_finished)
                return;

            // A step is one row from start to finish. Keyed by the assistant's own id rather than by
            // the tool name, so two reads of two different servers stay two rows.
            var at = _steps.FindIndex(s => s.Id == value.Id);
            if (at >= 0)
                _steps[at] = value;
            else
                _steps.Add(value);

            if (DateTimeOffset.UtcNow - _lastEdit < EditFloor)
                return;

            _lastEdit = DateTimeOffset.UtcNow;
            Dispatch(Render());
        }
    }

    /// <summary>
    /// Replaces the message with the turn's outcome and stops narrating.
    /// </summary>
    /// <remarks>
    /// Always published, whatever the coalescing window would have said — this is the state the
    /// message keeps for as long as anybody reads the thread, and a floor that could swallow it would
    /// leave the account of a finished turn stopped mid-step.
    /// </remarks>
    /// <param name="headline">What the turn came to, replacing the working headline.</param>
    /// <param name="body">
    /// The answer itself, kept in this message when the whole thing fits. Null keeps the message to
    /// the account of the work.
    /// </param>
    /// <returns>
    /// Whether <paramref name="body"/> went into this message. False means the caller still owes it a
    /// message of its own — the answer is what somebody is waiting for, and it is never the thing
    /// that gets truncated to make room for a list of steps.
    /// </returns>
    public async Task<bool> FinishAsync(string headline, string? body = null)
    {
        Task pending;
        lock (_gate)
        {
            _finished = true;
            pending = _pending;
        }

        // Let whatever edit is in flight land first, so it cannot overwrite the outcome behind it.
        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A live activity edit failed before the outcome was written ({What}).", _what);
        }

        var (text, carriedBody) = Final(headline, body);

        if (_message is null)
            return false;

        try
        {
            var result = await _queue.SendAsync(
                $"{_what} (outcome)",
                SendLane.Announcement,
                () => _message.ModifyAsync(m => m.Content = text));

            if (result is null || result.IsFailure)
            {
                _logger.LogDebug(
                    "Could not write the outcome onto the live activity message ({What}): {Reason}.",
                    _what, result?.Error ?? "the send queue answered with nothing");
                return false;
            }

            return carriedBody;
        }
        catch (Exception ex)
        {
            // Reported as not written, so the caller posts the answer itself. Claiming it landed would
            // leave the one message somebody is waiting for existing nowhere.
            _logger.LogDebug(ex, "Could not write the outcome onto the live activity message ({What}).", _what);
            return false;
        }
    }

    /// <summary>
    /// The finished message, and whether the body fitted inside it.
    /// </summary>
    private (string Text, bool CarriedBody) Final(string headline, string? body)
    {
        var steps = RenderWith(headline);

        if (string.IsNullOrWhiteSpace(body))
            return (Truncate(steps), false);

        var together = steps + "\n\n" + body;
        return together.Length <= DiscordMessageLimit
            ? (together, true)
            : (Truncate(steps), false);
    }

    /// <summary>The steps as they stand, for a caller that wants to keep them beside the outcome.</summary>
    public IReadOnlyList<AssistantActivity> Steps
    {
        get { lock (_gate) { return [.. _steps]; } }
    }

    /// <summary>Renders the current state — the working headline, then a row per step.</summary>
    public string Render() => RenderWith(_headline);

    /// <summary>Renders the steps under <paramref name="headline"/>.</summary>
    private string RenderWith(string headline)
    {
        var builder = new StringBuilder(headline);

        var steps = _steps.Count <= MaxRows ? _steps : _steps[^MaxRows..];
        var hidden = _steps.Count - steps.Count;

        if (hidden > 0)
            builder.Append("\n… ").Append(hidden).Append(hidden == 1 ? " earlier step" : " earlier steps");

        foreach (var step in steps)
        {
            builder.Append('\n').Append(Marker(step.State)).Append(' ').Append(step.Label);

            if (!string.IsNullOrWhiteSpace(step.Subject))
                builder.Append(" — ").Append(step.Subject);

            if (!string.IsNullOrWhiteSpace(step.Detail))
                builder.Append(" (").Append(step.Detail).Append(')');
        }

        return Truncate(builder.ToString());
    }

    private static string Marker(AssistantActivityState state) => state switch
    {
        AssistantActivityState.Running => "⏳",
        AssistantActivityState.Done => "✓",
        _ => "⚠️",
    };

    /// <summary>
    /// Queues an edit behind whatever is already in flight, so two edits cannot land out of order and
    /// leave the message showing a state the turn has already moved past.
    /// </summary>
    private void Dispatch(string text)
    {
        if (_message is null)
            return;

        _pending = _pending.ContinueWith(
            async _ =>
            {
                try
                {
                    var result = await _queue.SendAsync(
                        _what, SendLane.Announcement, () => _message.ModifyAsync(m => m.Content = text));

                    if (result is not null && result.IsFailure)
                        _logger.LogDebug("Could not edit the live activity message ({What}): {Reason}.", _what, result.Error);
                }
                catch (Exception ex)
                {
                    // A dropped edit costs one frame of the account and nothing else — the next one
                    // renders the whole current state, so the message catches up by itself.
                    _logger.LogDebug(ex, "Could not edit the live activity message ({What}).", _what);
                }
            },
            TaskScheduler.Default).Unwrap();
    }

    private static string Truncate(string text) =>
        text.Length <= DiscordMessageLimit ? text : text[..(DiscordMessageLimit - 1)] + "…";

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            _finished = true;
            pending = _pending;
        }

        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A live activity edit failed while finishing ({What}).", _what);
        }
    }
}
