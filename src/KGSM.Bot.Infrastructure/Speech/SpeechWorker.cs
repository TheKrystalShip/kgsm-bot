using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Speech;

/// <summary>
/// The bot's end of the speech worker: starts it, talks to it, and is the only thing that knows it
/// is a process at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing above this can tell.</b> Recognition and synthesis are asked for through the same two
/// interfaces they always were; this is where they turn into bytes on a socket. That is what makes the
/// arrangement reversible — a host that wanted the models in-process again would replace two classes
/// and change nothing else.
/// </para>
/// <para>
/// <b>Started when the bot joins a channel, and kept.</b> Loading costs about three seconds and the
/// first sentence after that is slower again, so tearing it down between questions would put that on
/// somebody every time. It stays up once started; a host that would rather have the memory back sets
/// <see cref="VoiceOptions.WorkerIdleMinutes"/> and pays the reload on the next join.
/// </para>
/// <para>
/// <b>A worker that will not start is not an outage.</b> The bot goes on listening in text and
/// answering in the channel, which is exactly what it does on a host with no model files — the same
/// degraded shape, reached a different way.
/// </para>
/// </remarks>
internal sealed class SpeechWorker : ISpeechEngine, IDisposable, IAsyncDisposable
{
    /// <summary>How long to wait for a worker to load its models and say it is ready.</summary>
    /// <remarks>
    /// Generous on purpose: loading is around three seconds warm, and a first run after a deploy pages
    /// several hundred megabytes of CUDA libraries off the disk. What this is really guarding is a
    /// worker that has hung, not one that is slow.
    /// </remarks>
    private static readonly TimeSpan ReadyWithin = TimeSpan.FromSeconds(90);

    /// <summary>How long any one request may take before the caller is told it failed.</summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(60);

    /// <summary>How long to leave a failed start alone before trying again.</summary>
    /// <remarks>
    /// A host with a broken driver fails every time, and the audio path asks on every utterance. This
    /// is what keeps that from becoming a process spawned per sentence.
    /// </remarks>
    private static readonly TimeSpan BeforeRetrying = TimeSpan.FromSeconds(60);

    private readonly VoiceOptions _voice;
    private readonly ILogger<SpeechWorker> _logger;
    private readonly SemaphoreSlim _starting = new(1, 1);

    private Live? _live;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;
    private uint _nextId;
    private Timer? _idle;
    private bool _disposed;

    public SpeechWorker(IOptions<DiscordOptions> options, ILogger<SpeechWorker> logger)
    {
        _voice = options.Value.Voice;
        _logger = logger;
    }

    /// <summary>Whether this host is configured and equipped to run one at all.</summary>
    /// <remarks>
    /// Answered from the files on disk rather than from a worker, because it is asked before there is
    /// one — a bot that reported no recogniser until it had started a process would report none at the
    /// moment it needed to start one.
    /// </remarks>
    public bool CanHear => _voice.Enabled && File.Exists(_voice.ModelPath);

    public bool CanSpeak =>
        _voice.Enabled && _voice.Speak && File.Exists(_voice.SpeechModelPath) && InstalledVoices.All().Any();

    public void Wake()
    {
        if (!CanHear || _disposed) return;

        // Cancelled rather than left to fire: a join arriving inside the idle window means the worker
        // is wanted again, and a timer that went off afterwards would stop the one now in use.
        _idle?.Dispose();
        _idle = null;

        // Not awaited. This is called from the join path, and the point of starting here rather than
        // on the first utterance is that loading overlaps with people settling into the channel.
        _ = Task.Run(async () =>
        {
            try
            {
                await ConnectedAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Speech: could not start the worker on joining");
            }
        });
    }

    public void Idle()
    {
        if (_voice.WorkerIdleMinutes <= 0 || _disposed) return;

        _idle?.Dispose();
        _idle = new Timer(
            _ => _ = StopAsync("nobody has needed it since the last channel emptied"),
            null,
            TimeSpan.FromMinutes(_voice.WorkerIdleMinutes),
            Timeout.InfiniteTimeSpan);
    }

    public async Task<(SpeechProtocol.Outcome Outcome, string Text)> TranscribeAsync(
        byte[] pcm, string vocabulary, bool ifIdle, CancellationToken ct = default)
    {
        (SpeechProtocol.Outcome outcome, byte[] payload) = await AskAsync(
            SpeechProtocol.Kind.Transcribe,
            id => SpeechProtocol.Transcribe(id, ifIdle, vocabulary, pcm),
            ct);

        if (outcome != SpeechProtocol.Outcome.Done) return (outcome, string.Empty);

        (_, SpeechProtocol.Outcome said, string text) = SpeechProtocol.ReadTranscribed(payload);
        return (said, text);
    }

    public async Task<byte[]?> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        (SpeechProtocol.Outcome outcome, byte[] payload) = await AskAsync(
            SpeechProtocol.Kind.Synthesize,
            id => SpeechProtocol.Synthesize(id, voice, text),
            ct);

        if (outcome != SpeechProtocol.Outcome.Done) return null;

        (_, SpeechProtocol.Outcome said, byte[] audio) = SpeechProtocol.ReadSynthesized(payload);
        return said == SpeechProtocol.Outcome.Done ? audio : null;
    }

    /// <summary>
    /// Sends one request and waits for the answer with its id on it.
    /// </summary>
    /// <returns>
    /// <see cref="SpeechProtocol.Outcome.Unavailable"/> when there was no worker to ask, which every
    /// caller treats the same way it treats a host with no model at all.
    /// </returns>
    private async Task<(SpeechProtocol.Outcome, byte[])> AskAsync(
        SpeechProtocol.Kind kind, Func<uint, byte[]> compose, CancellationToken ct)
    {
        Live? live;
        try
        {
            live = await ConnectedAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech: no worker to ask");
            return (SpeechProtocol.Outcome.Unavailable, []);
        }

        if (live is null) return (SpeechProtocol.Outcome.Unavailable, []);

        uint id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Pending[id] = answer;

        try
        {
            await live.SendAsync(kind, compose(id), ct);

            using var late = CancellationTokenSource.CreateLinkedTokenSource(ct);
            late.CancelAfter(AnswerWithin);

            byte[] payload = await answer.Task.WaitAsync(late.Token);
            return (SpeechProtocol.Outcome.Done, payload);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Speech: the worker did not answer a {Kind} request in time", kind);
            return (SpeechProtocol.Outcome.Failed, []);
        }
        catch (OperationCanceledException)
        {
            return (SpeechProtocol.Outcome.Failed, []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech: could not put a {Kind} request to the worker", kind);
            return (SpeechProtocol.Outcome.Failed, []);
        }
        finally
        {
            live.Pending.TryRemove(id, out _);
        }
    }

    /// <summary>The live worker, starting one if there is none. Null when this host cannot run one.</summary>
    private async Task<Live?> ConnectedAsync(CancellationToken ct)
    {
        Live? live = _live;
        if (live is { Healthy: true }) return live;

        if (!CanHear || _disposed) return null;

        await _starting.WaitAsync(ct);
        try
        {
            live = _live;
            if (live is { Healthy: true }) return live;

            if (DateTimeOffset.UtcNow - _failedAt < BeforeRetrying) return null;

            _live = null;
            live?.Dispose();

            try
            {
                _live = await StartAsync(ct);
                _failedAt = DateTimeOffset.MinValue;
                return _live;
            }
            catch (Exception ex)
            {
                _failedAt = DateTimeOffset.UtcNow;
                _logger.LogError(
                    ex, "Speech: could not start the speech worker — the bot will answer in text only");
                return null;
            }
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>
    /// Listens, starts a copy of this binary, and waits for it to connect back and say it is loaded.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The bot listens and the worker connects</b>, which is the opposite of the obvious way
    /// round and is what removes the race: there is no window in which the worker's socket does not
    /// exist yet, and no polling for a file to appear. The path carries this process's id, so two bots
    /// on one host — a service and somebody running one by hand — cannot collide.
    /// </remarks>
    private async Task<Live> StartAsync(CancellationToken ct)
    {
        string path = SocketPath();
        File.Delete(path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Socket? accepted = null;
        Process? worker = null;

        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);

            string binary = Environment.ProcessPath
                ?? throw new InvalidOperationException("This process has no path to start a copy of.");

            var start = new ProcessStartInfo(binary)
            {
                // The worker inherits stdout and stderr, so its log lines land in the same journal
                // under the same unit — a worker whose output went somewhere else would make the one
                // thing worth reading (why it could not load a model) invisible.
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add(SpeechWorkerHost.Flag);
            start.ArgumentList.Add(path);

            var timer = Stopwatch.StartNew();
            worker = Process.Start(start)
                ?? throw new InvalidOperationException("The speech worker did not start.");

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waiting.CancelAfter(ReadyWithin);

            accepted = await listener.AcceptAsync(waiting.Token);

            var live = new Live(worker, accepted, listener, path, _logger);
            live.Listen();

            (bool canHear, bool canSpeak, string detail) = await live.Ready.Task.WaitAsync(waiting.Token);
            timer.Stop();

            _logger.LogInformation(
                "Speech: worker {Pid} ready in {Elapsed}ms — {Detail}{Silent}",
                worker.Id, timer.ElapsedMilliseconds, detail,
                canHear && canSpeak ? string.Empty : " (partly unavailable — see its own log)");

            return live;
        }
        catch
        {
            accepted?.Dispose();
            listener.Dispose();

            if (worker is { HasExited: false })
            {
                // A worker that never connected is holding whatever it managed to load, and nothing
                // will ever talk to it. It has no other way to find out that it is not wanted.
                try { worker.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }

            worker?.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Stops the worker, giving the memory and the card back to the host.</summary>
    public async Task StopAsync(string why)
    {
        Live? live = Interlocked.Exchange(ref _live, null);
        if (live is null) return;

        _logger.LogInformation("Speech: stopping worker {Pid} — {Why}", live.Pid, why);
        await live.StopAsync();
    }

    /// <summary>
    /// Where the socket goes: the runtime directory when systemd gave us one, and the temp directory
    /// otherwise. Never the install prefix, which a deploy syncs with <c>--delete</c>.
    /// </summary>
    private static string SocketPath()
    {
        string directory =
            Environment.GetEnvironmentVariable("RUNTIME_DIRECTORY")?.Split(':').FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? Path.GetTempPath();

        return Path.Combine(directory, $"kgsm-bot-speech-{Environment.ProcessId}.sock");
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _idle?.Dispose();
        await StopAsync("the bot is shutting down");
        _starting.Dispose();
    }

    /// <summary>One running worker: the process, the connection to it, and what is outstanding on it.</summary>
    private sealed class Live(Process worker, Socket socket, Socket listener, string path, ILogger logger)
        : IDisposable
    {
        private readonly NetworkStream _stream = new(socket, ownsSocket: false);
        private readonly SemaphoreSlim _writing = new(1, 1);
        private readonly CancellationTokenSource _reading = new();
        private volatile bool _gone;

        public ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> Pending { get; } = new();

        public TaskCompletionSource<(bool CanHear, bool CanSpeak, string Detail)> Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Pid => worker.Id;

        /// <remarks>
        /// The process is asked, and being unable to ask counts as gone: a worker stopped by the idle
        /// timer at the moment an utterance arrives leaves this holding a disposed handle, and the
        /// caller wants "start another one", not an exception out of the audio path.
        /// </remarks>
        public bool Healthy
        {
            get
            {
                if (_gone) return false;

                try
                {
                    return !worker.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        /// <summary>Reads answers until the worker stops sending them.</summary>
        public void Listen() => _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    (SpeechProtocol.Kind Kind, byte[] Payload)? frame =
                        await SpeechProtocol.ReadAsync(_stream, _reading.Token);

                    if (frame is null) break;

                    switch (frame.Value.Kind)
                    {
                        case SpeechProtocol.Kind.Ready:
                            Ready.TrySetResult(SpeechProtocol.ReadReady(frame.Value.Payload));
                            break;

                        case SpeechProtocol.Kind.Transcribed:
                            Finish(SpeechProtocol.ReadTranscribed(frame.Value.Payload).Id, frame.Value.Payload);
                            break;

                        case SpeechProtocol.Kind.Synthesized:
                            Finish(SpeechProtocol.ReadSynthesized(frame.Value.Payload).Id, frame.Value.Payload);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Speech: lost the connection to worker {Pid}", Pid);
            }
            finally
            {
                _gone = true;

                // Everything outstanding is now never going to be answered. Failing them here is what
                // turns a worker that died into one slow answer rather than a voice channel where
                // nothing ever comes back.
                Ready.TrySetCanceled();
                foreach (uint id in Pending.Keys)
                    if (Pending.TryRemove(id, out TaskCompletionSource<byte[]>? waiting))
                        waiting.TrySetCanceled();
            }
        });

        private void Finish(uint id, byte[] payload)
        {
            if (Pending.TryRemove(id, out TaskCompletionSource<byte[]>? waiting))
                waiting.TrySetResult(payload);
        }

        public async Task SendAsync(SpeechProtocol.Kind kind, byte[] payload, CancellationToken ct)
        {
            await _writing.WaitAsync(ct);
            try
            {
                await SpeechProtocol.WriteAsync(_stream, kind, payload, ct);
            }
            finally
            {
                _writing.Release();
            }
        }

        /// <summary>
        /// Closes the connection and waits for the worker to notice.
        /// </summary>
        /// <remarks>
        /// Closing is the request: the worker's read loop ends at EOF and it exits on its own, which is
        /// also what happens if this process dies without asking. The kill is for a worker wedged
        /// inside a native call, where there is nothing else left to try.
        /// </remarks>
        public async Task StopAsync()
        {
            _gone = true;
            Dispose();

            try
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await worker.WaitForExitAsync(patience.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Speech: worker {Pid} did not stop when asked — killing it", Pid);
                try { worker.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            finally
            {
                worker.Dispose();
            }
        }

        public void Dispose()
        {
            _reading.Cancel();
            _stream.Dispose();
            socket.Dispose();
            listener.Dispose();
            _writing.Dispose();

            try { File.Delete(path); } catch { /* the socket file is not worth an exception */ }
        }
    }
}
