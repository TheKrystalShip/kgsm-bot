using System.Threading.Channels;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Speech;

namespace KGSM.Bot.Discord.Voice;

/// <summary>
/// Speaks one answer sentence by sentence, as the assistant writes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first sentence goes out while the model is still producing the rest.</b> A long answer is
/// most of a minute of generation, and waiting for the whole of it before synthesising any of it
/// spends that minute in silence in front of somebody who asked a question out loud. What is spoken
/// is unchanged — the whole reply, in the order it was written, markup stripped — and only when each
/// part of it goes out is different.
/// </para>
/// <para>
/// <b>Pieces are spoken one at a time, in order.</b> The slices arrive on the thread reading the
/// assistant's frames and are handed to a single reader that synthesises and plays each in turn, so
/// nothing here depends on the order two waiters are released in. The queue is unbounded because its
/// producer is the reply itself: a bounded one would either drop a sentence of the answer or block
/// the frame reader, and neither is a thing to do to somebody's question.
/// </para>
/// <para>
/// ⚠ <b>Everything spoken for the turn rides the same recital</b>, including the sentence pointing at
/// a staged action's buttons and the sentence said when a turn fails. Cutting the bot off drops a
/// recital whole, so anything spoken outside it would carry on over the person who cut in.
/// </para>
/// <para>
/// <b>Best-effort, like everything else spoken here.</b> No synthesiser, no card, or a broken output
/// stream costs the audio and nothing else — the answer is in the channel either way, which is why
/// nothing on this path is allowed to fail a turn.
/// </para>
/// </remarks>
internal sealed class SpokenRecital : IProgress<string>, IAsyncDisposable
{
    private readonly IVoiceRecital _recital;
    private readonly ITextToSpeech _speech;
    private readonly ILogger _logger;
    private readonly string _speaker;

    private readonly SpokenSentences _sentences = new();
    private readonly Channel<string> _pieces = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Task _reciting;

    /// <summary>How much of the reply itself has been handed over to be spoken.</summary>
    private int _wrote;

    /// <summary>Opens a recital that starts once <paramref name="after"/> has been heard.</summary>
    /// <remarks>
    /// The acknowledgement is played from a task started before the question was even put, and waiting
    /// for it is what keeps the first sentence of the answer from arriving on top of it — the two are
    /// said in the same channel and neither repeats the other.
    /// </remarks>
    public SpokenRecital(
        IVoiceRecital recital, ITextToSpeech speech, Task after, string speaker, ILogger logger,
        CancellationToken ct)
    {
        _recital = recital;
        _speech = speech;
        _speaker = speaker;
        _logger = logger;

        _reciting = Task.Run(() => ReciteAsync(after, ct), CancellationToken.None);
    }

    /// <summary>Takes the next slice of the reply and speaks whatever it completes.</summary>
    public void Report(string slice)
    {
        foreach (string sentence in _sentences.Take(slice))
        {
            Interlocked.Increment(ref _wrote);
            _pieces.Writer.TryWrite(sentence);
        }
    }

    /// <summary>
    /// Speaks whatever the reply ended on, and says whether any of the reply was spoken at all.
    /// </summary>
    /// <remarks>
    /// A reply whose last sentence never reached a full stop still gets said. False means no slice of
    /// the reply ever arrived — an assistant whose frames carry no reply text — and the caller then
    /// speaks the finished answer whole, which is the shape a turn always had.
    /// </remarks>
    public bool Flush()
    {
        if (_sentences.Flush() is { } rest)
        {
            Interlocked.Increment(ref _wrote);
            _pieces.Writer.TryWrite(rest);
        }

        return Volatile.Read(ref _wrote) > 0;
    }

    /// <summary>Adds something to say that is this surface's own, not part of the assistant's reply.</summary>
    public void Say(string? sentence)
    {
        if (SpokenSentences.Whole(sentence) is { Length: > 0 } spoken)
            _pieces.Writer.TryWrite(spoken);
    }

    /// <summary>Waits for everything handed over to have been said, or dropped.</summary>
    public async Task FinishAsync()
    {
        _pieces.Writer.TryComplete();
        await _reciting;
    }

    public async ValueTask DisposeAsync()
    {
        _pieces.Writer.TryComplete();

        // Awaited rather than abandoned: the reader owns the recital's place in the queue, and
        // letting go of it while it is still playing leaves the next answer racing this one.
        try { await _reciting; }
        catch (Exception ex) { _logger.LogDebug(ex, "Voice: the recital for {Speaker} ended badly", _speaker); }

        _recital.Dispose();
    }

    private async Task ReciteAsync(Task after, CancellationToken ct)
    {
        try { await after; }
        catch (Exception ex) { _logger.LogDebug(ex, "Voice: could not acknowledge {Speaker}", _speaker); }

        try
        {
            await foreach (string sentence in _pieces.Reader.ReadAllAsync(ct))
            {
                // Asked before the synthesis rather than only after it: somebody who cut in half a
                // minute ago is owed nothing further, and a card is not spent producing audio the
                // session is about to refuse.
                if (!_recital.Current)
                {
                    _logger.LogDebug(
                        "Voice: {Speaker}'s answer was cut off — the rest of it was not said", _speaker);
                    return;
                }

                byte[]? audio = await _speech.SynthesizeAsync(sentence, ct);
                if (audio is null) continue;

                Result said = await _recital.SayAsync(audio, ct);
                if (said.IsFailure)
                    _logger.LogDebug(
                        "Voice: could not say part of the answer to {Speaker}: {Reason}",
                        _speaker, said.Error);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The answer is in the channel; a recital that falls over must not take the turn with it.
            _logger.LogWarning(ex, "Voice: the answer to {Speaker} could not be read out", _speaker);
        }
    }
}
