using System.Net.Sockets;

using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Speech;

/// <summary>
/// The speech worker: this binary, started again with <c>--speech</c>, holding the models.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is a second process at all.</b> Whisper and Kokoro on the card cost about 1.6GB of
/// this host's memory, and neither gives it back when disposed — the CUDA runtime behind them stays
/// resident for the life of the process no matter what is released. A process that ends releases
/// everything, so the models live somewhere that can end without the bot ending: the bot idles at
/// 145MB, and the memory and the video memory both come back when this exits.
/// </para>
/// <para>
/// <b>It is the same binary and the same configuration.</b> Not a second artifact to deploy, not a
/// second version to keep in step — the bot starts a copy of itself with a socket to talk on, and the
/// copy reads the settings file the bot read. A worker can therefore never be out of date with
/// respect to the bot that started it.
/// </para>
/// <para>
/// <b>It ends when the connection does.</b> The bot stopping it, the bot crashing, the bot being
/// restarted by a deploy — all three reach here as a closed socket, and all three should leave nothing
/// behind. There is no other exit and no supervision loop: an orphaned worker holding a gigabyte and a
/// slice of the card is exactly what this design exists to prevent.
/// </para>
/// </remarks>
public static class SpeechWorkerHost
{
    /// <summary>The flag the bot starts this with, followed by the socket to connect back on.</summary>
    public const string Flag = "--speech";

    /// <summary>
    /// Serves one bot until it goes away.
    /// </summary>
    /// <returns>The process exit code: zero for a connection that ended, non-zero for one that never began.</returns>
    public static async Task<int> RunAsync(string socketPath, VoiceOptions voice, ILoggerFactory loggers)
    {
        ILogger logger = loggers.CreateLogger("KGSM.Bot.Speech");

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speech: could not reach the bot on {Socket}", socketPath);
            return 1;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);

        // Loaded before the bot is told anything, so "ready" means ready. The bot is waiting on this
        // to decide whether it can hear at all, and a worker that announced itself and then spent four
        // seconds loading would have the first utterance of every session arrive at a model that is
        // not there yet.
        using var ears = new SpeechRecogniser(voice.ModelPath, voice.UseGpu, logger);
        using var mouth = voice.Speak
            ? new SpeechSynthesiser(voice.SpeechModelPath, voice.SpeakUseGpu, voice.SpeechVoice, logger)
            : null;

        var writing = new SemaphoreSlim(1, 1);
        await SendAsync(stream, writing, SpeechProtocol.Kind.Ready,
            SpeechProtocol.Ready(ears.IsAvailable, mouth?.IsAvailable == true, Describe(voice)),
            CancellationToken.None);

        logger.LogInformation(
            "Speech: worker {Pid} serving — {Hearing}, {Speaking}",
            Environment.ProcessId,
            ears.IsAvailable ? "hearing" : "deaf",
            mouth?.IsAvailable == true ? "speaking" : "silent");

        var work = new CancellationTokenSource();
        try
        {
            while (true)
            {
                (SpeechProtocol.Kind Kind, byte[] Payload)? frame =
                    await SpeechProtocol.ReadAsync(stream, work.Token);

                // The bot has gone. Nothing to clean up that ending does not clean up better.
                if (frame is null) break;

                // Each request runs off the read loop: recognition and synthesis are both blocking
                // work of hundreds of milliseconds, and doing either here would stop the worker
                // hearing the next request until it finished.
                _ = Task.Run(() => AnswerAsync(
                    stream, writing, frame.Value.Kind, frame.Value.Payload,
                    ears, mouth, voice.SpeechVoice, logger, work.Token), work.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Speech: the connection to the bot failed");
        }
        finally
        {
            await work.CancelAsync();
        }

        logger.LogInformation("Speech: worker {Pid} stopping — the bot let go", Environment.ProcessId);
        return 0;
    }

    private static async Task AnswerAsync(
        Stream stream,
        SemaphoreSlim writing,
        SpeechProtocol.Kind kind,
        byte[] payload,
        SpeechRecogniser ears,
        SpeechSynthesiser? mouth,
        string defaultVoice,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case SpeechProtocol.Kind.Transcribe:
                {
                    (uint id, bool ifIdle, string vocabulary, byte[] audio) =
                        SpeechProtocol.ReadTranscribe(payload);

                    (SpeechProtocol.Outcome outcome, string text) =
                        await ears.ReadAsync(audio, vocabulary, ifIdle, ct);

                    await SendAsync(stream, writing, SpeechProtocol.Kind.Transcribed,
                        SpeechProtocol.Transcribed(id, outcome, text), ct);
                    break;
                }

                case SpeechProtocol.Kind.Synthesize:
                {
                    (uint id, string named, string text) = SpeechProtocol.ReadSynthesize(payload);
                    string voice = string.IsNullOrWhiteSpace(named) ? defaultVoice : named;

                    (SpeechProtocol.Outcome outcome, byte[] audio) = mouth is null
                        ? (SpeechProtocol.Outcome.Unavailable, [])
                        : await mouth.SayAsync(text, voice, ct);

                    await SendAsync(stream, writing, SpeechProtocol.Kind.Synthesized,
                        SpeechProtocol.Synthesized(id, outcome, audio), ct);
                    break;
                }

                default:
                    // A message this worker does not know is the bot being newer than its own worker,
                    // which cannot happen — they are the same binary. Logged rather than fatal, because
                    // dropping one request is better than dropping the session it was part of.
                    logger.LogWarning("Speech: ignored a {Kind} message", kind);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The request is lost and the caller's own timeout will report it. Ending the worker over
            // one bad frame would take the whole voice session with it.
            logger.LogWarning(ex, "Speech: could not answer a {Kind} request", kind);
        }
    }

    /// <summary>
    /// Writes one message, one at a time.
    /// </summary>
    /// <remarks>
    /// Answers are written from whichever thread finished the work, so without this a recognition
    /// finishing mid-write of a synthesised answer would interleave its bytes into it and desynchronise
    /// the stream for good.
    /// </remarks>
    private static async Task SendAsync(
        Stream stream, SemaphoreSlim writing, SpeechProtocol.Kind kind, byte[] payload, CancellationToken ct)
    {
        await writing.WaitAsync(ct);
        try
        {
            await SpeechProtocol.WriteAsync(stream, kind, payload, ct);
        }
        finally
        {
            writing.Release();
        }
    }

    private static string Describe(VoiceOptions voice) =>
        $"{Path.GetFileName(voice.ModelPath)} on the {(voice.UseGpu ? "GPU" : "CPU")}, "
        + $"speaking as {voice.SpeechVoice} on the {(voice.SpeakUseGpu ? "GPU" : "CPU")}";
}
