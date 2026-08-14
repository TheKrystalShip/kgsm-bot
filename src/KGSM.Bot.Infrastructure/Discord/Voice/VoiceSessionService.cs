using System.Collections.Concurrent;

using Discord;
using Discord.Audio;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Holds the bot's voice connections and turns what it hears into utterances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Received audio arrives already decrypted and decoded.</b> Discord.Net hands over one stream
/// per speaker, keyed by Discord account id, having taken the packet through DAVE decryption and the
/// Opus decoder — so what arrives here is 48 kHz stereo PCM and who said it, and neither had to be
/// worked out. A frame that fails to decrypt is logged by the library and dropped; there is no
/// partial audio to reason about at this level.
/// </para>
/// <para>
/// <b>Nothing is written to disk.</b> An utterance is bytes in memory handed to a sink and then
/// released. This is a bot that hears a room, not one that records it, and the difference is
/// structural rather than a setting.
/// </para>
/// <para>
/// <b>The silence that ends a sentence has to be looked for, not waited for.</b> Frames stop
/// arriving when somebody stops talking, so the read loop simply blocks — it cannot notice the gap
/// it is sitting in. A ticker runs alongside and asks each assembler whether its speaker has been
/// quiet long enough, which is the only thing that ever closes an utterance during a conversation.
/// </para>
/// </remarks>
public sealed class VoiceSessionService : IVoiceSessions, IDisposable
{
    /// <summary>
    /// How often to look for a speaker who has stopped talking. It bounds the delay between the end
    /// of a sentence and the utterance being handed on, and adds to every answer's latency — but a
    /// tick that outruns the silence gap spends wake-ups finding nothing.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How much longer than its own duration a piece of audio may take to go out before the stream is
    /// treated as wedged.
    /// </summary>
    private static readonly TimeSpan SpeakingGrace = TimeSpan.FromSeconds(10);

    private readonly DiscordSocketClient _client;
    private readonly IVoiceUtteranceSink _sink;
    private readonly ISpeechEngine _speech;
    private readonly VoiceDecryptHealth _health;
    private readonly ILogger<VoiceSessionService> _logger;
    private readonly VoiceOptions _options;
    private readonly UtteranceLimits _limits;

    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();

    /// <summary>
    /// Reconnects spent on a broken session, per guild. Cleared by a join that then works long enough
    /// to be measured healthy, so an unrelated failure hours later gets its own full allowance.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, int> _rejoins = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public VoiceSessionService(
        DiscordSocketClient client,
        IVoiceUtteranceSink sink,
        ISpeechEngine speech,
        VoiceDecryptHealth health,
        IOptions<DiscordOptions> options,
        ILogger<VoiceSessionService> logger)
    {
        _client = client;
        _sink = sink;
        _speech = speech;
        _health = health;
        _logger = logger;
        _options = options.Value.Voice;
        _limits = new UtteranceLimits(
            SilenceGap: TimeSpan.FromMilliseconds(Math.Max(100, _options.SilenceGapMs)),
            MinDuration: TimeSpan.FromMilliseconds(Math.Max(100, _options.MinUtteranceMs)),
            MaxDuration: TimeSpan.FromSeconds(Math.Max(1, _options.MaxUtteranceSeconds)));
    }

    public bool IsEnabled => _options.Enabled;

    public VoiceSession? Describe(ulong guildId) =>
        _sessions.TryGetValue(guildId, out Session? session) ? session.Describe() : null;

    public async Task<Result<VoiceSession>> JoinAsync(
        ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return Result<VoiceSession>.Failure("Voice listening is switched off on this host.");

        if (_client.GetChannel(channelId) is not SocketVoiceChannel channel)
            return Result<VoiceSession>.Failure("That is not a voice channel I can see.");

        if (channel.Guild.Id != guildId)
            return Result<VoiceSession>.Failure("That voice channel belongs to a different Discord server.");

        // Asked before anything is attempted: joining a channel the bot cannot connect to produces a
        // failure from the library that reads as a fault rather than as a missing permission.
        if (!channel.Guild.CurrentUser.GetPermissions(channel).Connect)
            return Result<VoiceSession>.Failure($"I don't have permission to connect to **{channel.Name}**.");

        // Here rather than on the first thing anybody says, and here rather than after the handshake:
        // the engine takes a few seconds to load and this is the earliest moment it is known it will
        // be wanted, so it happens while Discord is negotiating the connection and people are settling
        // into the channel. It does not block the join, and a host with no speech engine still joins —
        // the bot listens and answers in the channel's chat. Nothing tells it when to unload: the
        // engine serves every surface on the host and idles out on its own schedule.
        _speech.Wake();

        await _gate.WaitAsync(ct);
        try
        {
            // Discord gives a bot one voice connection per guild, so a second join moves it. Leaving
            // first makes that explicit rather than letting the library replace a session underneath a
            // still-running set of read loops.
            if (_sessions.TryRemove(guildId, out Session? existing))
                await existing.DisposeAsync();

            // Neither deafened nor muted: it is here to hear the room and to answer out loud. A bot
            // that joins muted is one whose replies go nowhere, with no error to say so.
            IAudioClient audio = await channel.ConnectAsync(selfDeaf: false, selfMute: false);

            // A fresh connection means fresh keys, so nothing measured about the last one applies.
            _health.Reset();

            var session = new Session(
                channel, audio, _limits, _sink, _options, _health, _logger, LeaveAsync, RejoinAsync,
                () => _rejoins.TryRemove(channel.Guild.Id, out _));
            _sessions[guildId] = session;
            session.Start();

            _logger.LogInformation(
                "Voice: listening in {Channel} ({Guild}) — silence gap {Gap}ms, utterances {Min}-{Max}",
                channel.Name, channel.Guild.Name, _options.SilenceGapMs, _options.MinUtteranceMs,
                _options.MaxUtteranceSeconds * 1000);

            return Result<VoiceSession>.Success(session.Describe());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice: could not join channel {ChannelId}", channelId);
            return Result<VoiceSession>.Failure($"I couldn't join that channel: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> SpeakAsync(
        ulong guildId, byte[] pcm, TimeSpan? waitAtMost = null, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(guildId, out Session? session))
            return Result.Failure("I'm not in a voice channel here.");

        return await session.SpeakAsync(pcm, waitAtMost, partOf: null, ct);
    }

    public IVoiceRecital? BeginRecital(ulong guildId) =>
        _sessions.TryGetValue(guildId, out Session? session) ? session.BeginRecital() : null;

    public bool StopSpeaking(ulong guildId) =>
        _sessions.TryGetValue(guildId, out Session? session) && session.StopSpeaking();

    public async Task<Result> LeaveAsync(ulong guildId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_sessions.TryRemove(guildId, out Session? session))
                return Result.Failure("I'm not in a voice channel here.");

            await session.DisposeAsync();
            _logger.LogInformation("Voice: left {Channel}", session.ChannelName);

            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rebuilds a connection whose encryption has stopped working.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rejoining is the only lever available: the keys are negotiated during the handshake and there
    /// is no way to ask for new ones without one. It is the same call the person would make by hand
    /// with <c>/voice leave</c> and <c>/voice join</c>, so it fixes exactly what that fixes.
    /// </para>
    /// <para>
    /// <b>Bounded, and it says so when it gives up.</b> A session that cannot be repaired by
    /// reconnecting will not be repaired by reconnecting ten more times, and a bot that silently
    /// cycles a voice connection forever is worse than one that stops and reports. After the last
    /// attempt it leaves the channel rather than sitting in it deaf, because a bot present and
    /// hearing nothing is the thing that made this hard to notice in the first place.
    /// </para>
    /// </remarks>
    private async Task RejoinAsync(ulong guildId, ulong channelId)
    {
        const int MostAttempts = 3;

        int attempt = _rejoins.AddOrUpdate(guildId, 1, (_, n) => n + 1);

        if (attempt > MostAttempts)
        {
            _logger.LogError(
                "Voice: the connection in guild {Guild} keeps failing to decrypt after {Attempts} "
                + "reconnects — leaving the channel rather than sitting in it deaf", guildId, MostAttempts);

            _rejoins.TryRemove(guildId, out _);
            await LeaveAsync(guildId, CancellationToken.None);
            return;
        }

        _logger.LogWarning(
            "Voice: the encrypted session has stopped decrypting — rejoining (attempt {Attempt} of {Most})",
            attempt, MostAttempts);

        Result<VoiceSession> rejoined = await JoinAsync(guildId, channelId, CancellationToken.None);

        if (rejoined.IsFailure)
            _logger.LogError("Voice: could not rejoin after a failed session: {Reason}", rejoined.Error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (Session session in _sessions.Values)
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _sessions.Clear();
        _gate.Dispose();
    }

    /// <summary>One guild's connection, its speakers, and the loops feeding them.</summary>
    private sealed class Session(
        SocketVoiceChannel channel,
        IAudioClient audio,
        UtteranceLimits limits,
        IVoiceUtteranceSink sink,
        VoiceOptions options,
        VoiceDecryptHealth health,
        ILogger logger,
        Func<ulong, CancellationToken, Task<Result>> leave,
        Func<ulong, ulong, Task> rejoin,
        Action working)
    {
        private readonly ConcurrentDictionary<ulong, Speaker> _speakers = new();
        private readonly CancellationTokenSource _cts = new();

        // One output stream for the session, and one answer through it at a time. Made on first use
        // rather than on joining, so a session nobody speaks into never builds an encoder.
        private readonly SemaphoreSlim _mouth = new(1, 1);
        private AudioOutStream? _out;

        /// <summary>What is being said right now, or null when the bot is quiet.</summary>
        private Speech? _saying;

        /// <summary>
        /// The recital being spoken, or zero when no answer is part-way through being said.
        /// </summary>
        /// <remarks>
        /// An answer written as it is spoken is a sequence of pieces with gaps between them, so
        /// "is the bot talking" cannot be read off the piece in the air: between two sentences there
        /// is none, and a stop landing there would find nothing and let the rest play. This is what
        /// spans the gaps.
        /// </remarks>
        private long _reciting;
        private long _recitals;
        private readonly DateTimeOffset _joinedAt = DateTimeOffset.UtcNow;
        private long _utterances;

        /// <summary>
        /// How many audio frames have arrived, ever.
        /// </summary>
        /// <remarks>
        /// Counted because zero of them is a real and invisible failure. A bot that is connected and
        /// receiving nothing looks exactly like one that hears you and does not understand you —
        /// measured here as three minutes of somebody repeating a trigger phrase into a session that
        /// had never been sent a single packet. The connection reports itself healthy throughout,
        /// because on this side nothing has gone wrong.
        /// </remarks>
        private long _frames;
        private bool _warnedDeaf;

        /// <summary>How much of a sentence to read early, looking for the trigger. Zero is off.</summary>
        private readonly TimeSpan _early = TimeSpan.FromMilliseconds(Math.Max(0, options.EarlyTriggerMs));

        public string ChannelName => channel.Name;

        public void Start()
        {
            audio.StreamCreated += OnStreamCreated;
            audio.ClientDisconnected += OnClientDisconnected;

            // Streams for people already talking when the bot arrived exist before the event can be
            // subscribed to, so the current set is drained once rather than waited for.
            foreach ((ulong userId, AudioInStream stream) in audio.GetStreams())
                _ = OnStreamCreated(userId, stream);

            _ = Task.Run(() => TickAsync(_cts.Token));
        }

        /// <summary>Plays one answer, waiting for any answer already playing to finish.</summary>
        /// <remarks>
        /// <para>
        /// Flushed before the lock is released: Discord.Net buffers, and returning while audio is
        /// still queued would let the next answer start writing into the middle of this one.
        /// </para>
        /// <para>
        /// ⚠ <b>Anything shorter than the output buffer is padded to it.</b> Discord.Net's buffered
        /// writer transmits nothing at all until a full buffer's worth of frames has been queued —
        /// below that its sending loop waits, and the flush waits on the sending loop, so a short
        /// write never completes and never fails. Measured: a 290ms tone wedged the stream, the write
        /// never returned, and because requests are answered one at a time the whole surface went
        /// silent with no error anywhere. Trailing zeroes are silence, so padding costs the time it
        /// takes to drain and nothing that can be heard.
        /// </para>
        /// <para>
        /// <b>And the write is bounded anyway.</b> Padding fixes the cause that was measured; the
        /// timeout is what stops any other stall in the audio stack from freezing everything a person
        /// might ask afterwards. Nothing spoken out loud is worth that, since the answer is already in
        /// the chat.
        /// </para>
        /// </remarks>
        public async Task<Result> SpeakAsync(
            byte[] pcm, TimeSpan? waitAtMost, long? partOf, CancellationToken ct)
        {
            if (pcm.Length == 0) return Result.Success();

            if (partOf is { } before && Interlocked.Read(ref _reciting) != before)
                return Cut();

            byte[] audible = SendableAudio.AtLeastPreload(pcm);

            if (waitAtMost is { } limit)
            {
                if (!await _mouth.WaitAsync(limit, ct))
                    return Result.Failure("Something else was still playing.");
            }
            else
            {
                await _mouth.WaitAsync(ct);
            }

            // ⚠ Asked again, and this is the check that matters. A piece queued behind the sentence
            // before it spends that sentence's whole duration waiting here, and an interruption
            // arriving in that window is exactly the one a person makes — they cut in while the bot
            // is talking. Refusing only on the way in would let every piece already waiting play.
            if (partOf is { } still && Interlocked.Read(ref _reciting) != still)
            {
                _mouth.Release();
                return Cut();
            }

            TimeSpan budget = PcmUpsampler.DurationOfStereo48k(audible.Length) + SpeakingGrace;
            DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + budget;

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(budget);

            // Published only now: until the mouth is held there is nothing playing to cut off, and a
            // stop arriving before then would cancel an answer that had not started.
            var speech = new Speech(bounded);
            _saying = speech;

            try
            {
                _out ??= audio.CreatePCMStream(
                    AudioApplication.Voice, bufferMillis: SendableAudio.BufferMillis);

                await _out.WriteAsync(audible, speech.Token);
                await _out.FlushAsync(speech.Token);

                logger.LogDebug(
                    "Voice: said {Seconds:F1}s in {Channel}",
                    PcmUpsampler.DurationOfStereo48k(pcm.Length).TotalSeconds, channel.Name);

                return Result.Success();
            }
            catch (OperationCanceledException) when (speech.Stopped)
            {
                // ⚠ Cancelling stops the writing, not the sound. Up to a whole buffer of audio is
                // already queued and would keep playing for a second after somebody cut in — so the
                // stream is thrown away with what it was holding, and the next answer builds a new
                // one. That is the only way to drop queued frames: the writer's own Clear dequeues
                // them without releasing the slots they held, which starves the stream for good.
                Drop();

                logger.LogInformation(
                    "Voice: stopped part-way through {Seconds:F1}s of speech in {Channel} — somebody "
                    + "addressed the bot while it was talking",
                    PcmUpsampler.DurationOfStereo48k(audible.Length).TotalSeconds, channel.Name);

                return Result.Failure("Somebody cut in, so I stopped talking.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Result.Failure("I was interrupted before I could say that.");
            }
            catch (OperationCanceledException) when (DateTimeOffset.UtcNow >= giveUpAt)
            {
                // The budget really did run out: the stream took longer than the audio it was given
                // could possibly take. It is not coming back, so it is dropped rather than left for
                // the next answer to wait on too.
                logger.LogWarning(
                    "Voice: the audio stream in {Channel} stopped accepting {Seconds:F1}s of speech — "
                    + "rebuilding it for the next one",
                    channel.Name, PcmUpsampler.DurationOfStereo48k(audible.Length).TotalSeconds);

                Drop();
                return Result.Failure("I couldn't say that out loud.");
            }
            catch (OperationCanceledException)
            {
                // ⚠ Cancelled well inside the budget, so the stream is not the thing that failed —
                // the connection underneath it went away, and Discord.Net cancels an in-flight write
                // from the audio client's own token when it does. Measured: a voice disconnect
                // reported here as a wedged stream, which sends anybody reading the log after the
                // fact to the wrong half of the system.
                logger.LogInformation(
                    "Voice: the connection to {Channel} went away part-way through {Seconds:F1}s of "
                    + "speech — it was not said",
                    channel.Name, PcmUpsampler.DurationOfStereo48k(audible.Length).TotalSeconds);

                Drop();
                return Result.Failure("I lost the voice connection before I could say that.");
            }
            catch (Exception ex)
            {
                // A broken output stream is not a broken session: the connection may still be
                // delivering audio in the other direction, and the answer is already in the chat.
                logger.LogWarning(ex, "Voice: could not speak in {Channel}", channel.Name);
                Drop();
                return Result.Failure("I couldn't say that out loud.");
            }
            finally
            {
                // Cleared only if it is still ours: a stop takes the field on its way past, and the
                // next answer may already have claimed it.
                Interlocked.CompareExchange(ref _saying, null, speech);
                _mouth.Release();
            }
        }

        /// <summary>Opens a recital, which abandons whichever one was current.</summary>
        public IVoiceRecital BeginRecital()
        {
            long id = Interlocked.Increment(ref _recitals);
            Interlocked.Exchange(ref _reciting, id);
            return new Recital(this, id);
        }

        private bool IsCurrent(long id) => Interlocked.Read(ref _reciting) == id;

        /// <summary>Closes a recital, unless it has already been cut off or replaced.</summary>
        private void EndRecital(long id) => Interlocked.CompareExchange(ref _reciting, 0, id);

        private static Result Cut() =>
            Result.Failure("Somebody cut in, so the rest of that answer was not said.");

        /// <summary>
        /// Ends what is being said now, and the whole answer it was part of. False when the bot was
        /// neither talking nor part-way through one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Taken rather than read, so two things noticing the same interruption — the trigger spotted
        /// mid-sentence, and the same trigger read again from the finished one — stop one answer
        /// between them rather than reporting two.
        /// </para>
        /// <para>
        /// ⚠ <b>The recital goes first and it goes whether or not anything is playing.</b> An answer
        /// spoken as it is written is queued a sentence at a time, so the moment somebody cuts in is
        /// as likely to fall in the gap between two of them as inside one — and dropping only the
        /// sentence in the air would have the bot pause and then resume over the top of them.
        /// </para>
        /// </remarks>
        public bool StopSpeaking()
        {
            bool reciting = Interlocked.Exchange(ref _reciting, 0) != 0;

            Speech? saying = Interlocked.Exchange(ref _saying, null);
            saying?.Stop();

            return saying is not null || reciting;
        }

        /// <summary>
        /// Throws the output stream away, along with whatever it still had queued.
        /// </summary>
        /// <remarks>
        /// Disposed rather than dropped on the floor: the stream owns a loop that goes on pacing
        /// frames onto the connection until something cancels it, so letting go of the reference
        /// leaves a second writer running against the same connection as its replacement. Nothing
        /// below it is shared — each stream builds its own chain down to the socket — so this costs
        /// an encoder and touches nothing else.
        /// </remarks>
        private void Drop()
        {
            AudioOutStream? going = _out;
            _out = null;
            if (going is null) return;

            try { going.Dispose(); }
            catch (Exception ex) { logger.LogDebug(ex, "Voice: error closing the output stream"); }
        }


        public VoiceSession Describe() => new(
            channel.Guild.Id, channel.Id, channel.Name, _joinedAt,
            _speakers.Count, Interlocked.Read(ref _utterances),
            Interlocked.Read(ref _frames), Others(), DateTimeOffset.UtcNow - _joinedAt);

        /// <summary>How many people other than the bot are in the channel.</summary>
        private int Others() => channel.ConnectedUsers.Count(u => u.Id != channel.Guild.CurrentUser.Id);

        /// <summary>
        /// Says once, in the log, that the connection is up and carrying nothing.
        /// </summary>
        /// <remarks>
        /// Once per session and never again: it is a state rather than an event, and a warning per
        /// tick would be five a second. Nothing is done about it beyond saying so, because every
        /// cause is on the other side of the connection — a bot deafened in the guild, a muted
        /// microphone, or a voice server that handed out a session and routed no media. Reconnecting
        /// automatically would fix the third and mask the first two.
        /// </remarks>
        private void WarnIfDeaf()
        {
            if (_warnedDeaf || !Describe().HearsNothing) return;
            _warnedDeaf = true;

            logger.LogWarning(
                "Voice: connected to {Channel} with {Others} other people for {Seconds:F0}s and no audio "
                + "has arrived at all — check the bot is not server-deafened in {Guild}, that microphones "
                + "are not muted, and try leaving and rejoining",
                channel.Name, Others(), (DateTimeOffset.UtcNow - _joinedAt).TotalSeconds, channel.Guild.Name);
        }

        private Task OnStreamCreated(ulong userId, AudioInStream stream)
        {
            // The bot's own audio never comes back from Discord, so there is no self-filter here; a
            // stream with our id would mean something is echoing and is worth seeing rather than
            // hiding.
            string name = channel.Guild.GetUser(userId)?.DisplayName ?? userId.ToString();
            var speaker = _speakers.GetOrAdd(userId, id => new Speaker(
                new UtteranceAssembler(id, name, channel.Guild.Id, channel.Id, limits)));

            _ = Task.Run(() => PumpAsync(userId, name, stream, speaker, _cts.Token));
            return Task.CompletedTask;
        }

        private async Task OnClientDisconnected(ulong userId)
        {
            // Somebody who left is not coming back to finish their sentence, so what they had said is
            // taken now rather than waiting out a silence gap that will never end.
            if (_speakers.TryRemove(userId, out Speaker? speaker))
                await EmitAsync(speaker, force: true);
        }

        /// <summary>Drains one speaker's stream for as long as the session lasts.</summary>
        private async Task PumpAsync(
            ulong userId, string name, AudioInStream stream, Speaker speaker, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    RTPFrame frame = await stream.ReadFrameAsync(ct);
                    Interlocked.Increment(ref _frames);

                    // A frame that got here decrypted, which is the half of the health signal that
                    // says the keys are still right — and the proof that whatever reconnecting was
                    // spent on worked, so the next unrelated failure gets a full allowance again.
                    health.Received();
                    if (Interlocked.Read(ref _frames) == 1) working();
                    byte[] mono = PcmDownsampler.ToMono16k(frame.Payload);
                    if (mono.Length == 0) continue;

                    VoiceUtterance? full;
                    lock (speaker.Gate)
                        full = speaker.Assembler.Append(mono, DateTimeOffset.UtcNow);

                    // Only the ceiling closes an utterance from in here; every ordinary one is closed
                    // by the ticker, because the end of a sentence is an absence of frames and this
                    // loop only runs when there are frames.
                    if (full is not null) await HandAsync(full);

                    // Looked at here rather than on the tick, because the whole value of reading a
                    // sentence early is in when it happens: the tick runs five times a second, so
                    // deciding there spends up to another fifth of a second before anybody hears
                    // that they were understood. Costs one lock and a comparison per frame.
                    else PeekAt(speaker);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice: audio stream for {Speaker} ended", name);
            }
        }

        /// <summary>Closes utterances whose speaker has gone quiet, and handles an emptied channel.</summary>
        private async Task TickAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TickInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    foreach (Speaker speaker in _speakers.Values)
                        await EmitAsync(speaker, force: false);

                    WarnIfDeaf();

                    if (health.IsBroken(DateTimeOffset.UtcNow))
                    {
                        logger.LogWarning(
                            "Voice: frames are arriving in {Channel} and none of them decrypt — the "
                            + "encrypted session is dead, rebuilding it", channel.Name);

                        // Fire and forget for the same reason leaving is: rejoining disposes this
                        // session, and this loop is inside it.
                        _ = rejoin(channel.Guild.Id, channel.Id);
                        return;
                    }

                    if (options.LeaveWhenAlone && !ct.IsCancellationRequested && IsAlone())
                    {
                        logger.LogInformation("Voice: {Channel} is empty — leaving", channel.Name);
                        // Fire and forget: this runs inside the session's own loop, and leaving
                        // cancels the token this loop is waiting on.
                        _ = leave(channel.Guild.Id, CancellationToken.None);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Whether everybody else has gone. Read from the gateway's own view of the channel rather
        /// than from whether audio is arriving — somebody sitting silently is still somebody there.
        /// </summary>
        private bool IsAlone() =>
            channel.ConnectedUsers.All(u => u.Id == channel.Guild.CurrentUser.Id);

        /// <summary>
        /// Hands over the opening of a sentence still being spoken, so being addressed can be noticed
        /// before the speaker has finished addressing anybody.
        /// </summary>
        /// <remarks>
        /// <b>Started and not awaited.</b> Reading it takes a recognition pass, and this is the loop
        /// draining a live audio stream — waiting here would stop reading frames in order to look
        /// ahead at frames already read. The copy is taken under the lock; everything after that is
        /// off this loop.
        /// </remarks>
        private void PeekAt(Speaker speaker)
        {
            if (_early <= TimeSpan.Zero) return;

            VoiceUtterance? sofar;
            lock (speaker.Gate)
                sofar = speaker.Assembler.Peek(_early);

            if (sofar is not null) _ = HandAsync(sofar);
        }

        private async Task EmitAsync(Speaker speaker, bool force)
        {
            VoiceUtterance? utterance;
            lock (speaker.Gate)
                utterance = speaker.Assembler.Close(DateTimeOffset.UtcNow, force);

            if (utterance is not null) await HandAsync(utterance);
        }

        private async Task HandAsync(VoiceUtterance utterance)
        {
            // A partial is a copy of a sentence that has not happened yet, and counting it would
            // report a room as having said twice as much as it did.
            if (!utterance.Partial) Interlocked.Increment(ref _utterances);

            try
            {
                await sink.OnUtteranceAsync(utterance, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // A sink that throws must not take the voice connection down with it: the person is
                // still talking, and the next thing they say may well be handled fine.
                logger.LogError(ex, "Voice: sink threw on an utterance from {Speaker}", utterance.SpeakerName);
            }
        }

        public async ValueTask DisposeAsync()
        {
            audio.StreamCreated -= OnStreamCreated;
            audio.ClientDisconnected -= OnClientDisconnected;

            await _cts.CancelAsync();

            // Whatever each speaker had said is taken before the connection goes: it was heard, and
            // dropping it because the bot was told to leave loses a sentence somebody had finished.
            foreach (Speaker speaker in _speakers.Values)
            {
                VoiceUtterance? tail;
                lock (speaker.Gate)
                    tail = speaker.Assembler.Close(DateTimeOffset.UtcNow, force: true);

                if (tail is not null)
                {
                    try { await sink.OnUtteranceAsync(tail, CancellationToken.None); }
                    catch (Exception ex) { logger.LogError(ex, "Voice: sink threw draining {Speaker}", tail.SpeakerName); }
                }
            }

            _speakers.Clear();

            if (_out is not null)
            {
                try { await _out.DisposeAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Voice: error closing the output stream"); }
                _out = null;
            }

            _mouth.Dispose();

            try { await audio.StopAsync(); }
            catch (Exception ex) { logger.LogWarning(ex, "Voice: error closing the connection to {Channel}", channel.Name); }

            audio.Dispose();
            _cts.Dispose();
        }

        /// <summary>
        /// One answer on its way out, and the lever that ends it early.
        /// </summary>
        /// <remarks>
        /// <b>Why the flag and not just the token.</b> A write can be cancelled for three reasons that
        /// look identical where it is caught — the caller gave up, the budget ran out, or the
        /// connection went away — and each of those is a different thing to tell whoever reads the
        /// log afterwards. Being stopped on purpose is the fourth, and it is the only one that is not
        /// a fault at all.
        /// </remarks>
        private sealed class Speech(CancellationTokenSource source)
        {
            /// <summary>Whether it was ended deliberately.</summary>
            public bool Stopped { get; private set; }

            public CancellationToken Token => source.Token;

            public void Stop()
            {
                // Set first: the write may be cancelled and caught before this method returns, and it
                // reads this to know why.
                Stopped = true;

                // The source belongs to the write, which disposes it on the way out. Losing that race
                // means the answer had already finished — which is the outcome being asked for.
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// A handle on one answer being spoken in pieces.
        /// </summary>
        /// <remarks>
        /// It holds nothing but an id: the session owns which recital is current, so an interruption
        /// arriving anywhere invalidates this without having to find it.
        /// </remarks>
        private sealed class Recital(Session session, long id) : IVoiceRecital
        {
            public bool Current => session.IsCurrent(id);

            public Task<Result> SayAsync(byte[] pcm, CancellationToken ct = default) =>
                session.SpeakAsync(pcm, waitAtMost: null, partOf: id, ct);

            public void Dispose() => session.EndRecital(id);
        }

        /// <summary>
        /// One speaker's assembler and the lock over it — the read loop appends while the ticker
        /// closes, and an assembler holding a growing buffer is not safe across the two.
        /// </summary>
        private sealed class Speaker(UtteranceAssembler assembler)
        {
            public UtteranceAssembler Assembler { get; } = assembler;
            public object Gate { get; } = new();
        }
    }
}
