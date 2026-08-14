using System.Text;

namespace KGSM.Bot.Infrastructure.Speech;

/// <summary>
/// What the bot and its speech worker say to each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two questions and their answers, and nothing else.</b> Audio in and words out, words in and
/// audio out. The worker holds no state worth naming: which voice to speak in and which names to
/// expect travel with each request, so a worker that has just started answers exactly as one that
/// has been up for a day, and losing one costs nothing but the reload.
/// </para>
/// <para>
/// <b>Every message carries an id and replies may arrive out of order.</b> Recognition and synthesis
/// run at the same time in there — somebody's next sentence is being read while the answer to their
/// last one is being spoken — so a protocol that made the connection a queue would serialise two
/// things that are not.
/// </para>
/// </remarks>
internal static class SpeechProtocol
{
    /// <summary>
    /// The largest frame either end will read.
    /// </summary>
    /// <remarks>
    /// Audio is the only thing here with any size to it, and the ceiling on that is an utterance —
    /// twenty seconds of 16 kHz mono is 640KB, and a spoken answer a few times that. This is not a
    /// tuning knob: it is the point past which a length prefix is more likely to be a desynchronised
    /// stream than a real message, and reading it would mean allocating whatever the noise said.
    /// </remarks>
    private const int LargestFrame = 32 * 1024 * 1024;

    internal enum Kind : byte
    {
        /// <summary>Worker to bot, once, unasked: what this host turned out to be able to do.</summary>
        Ready = 1,
        Transcribe = 2,
        Transcribed = 3,
        Synthesize = 4,
        Synthesized = 5,
    }

    /// <summary>Why a request came back without what was asked for.</summary>
    internal enum Outcome : byte
    {
        /// <summary>It worked. For recognition, empty text means nothing was said, which is normal.</summary>
        Done = 0,

        /// <summary>
        /// The recogniser was already running and the caller said not to wait. Distinct from
        /// <see cref="Done"/> with no text, because one means "not heard" and this means "not tried".
        /// </summary>
        Busy = 1,

        /// <summary>There is no model loaded to ask — a misconfigured host, not a failed request.</summary>
        Unavailable = 2,

        /// <summary>It was attempted and it threw. The worker logs the detail; the bot carries on.</summary>
        Failed = 3,
    }

    /// <summary>Reads one frame, or null when the other end has gone away.</summary>
    /// <remarks>
    /// A closed connection is the ordinary end of both processes' interest in each other — the bot
    /// stopping the worker, or the worker's parent dying — so it is reported as an absence of a
    /// message rather than as an error either end has to catch.
    /// </remarks>
    internal static async Task<(Kind Kind, byte[] Payload)?> ReadAsync(
        Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[5];
        if (!await FillAsync(stream, header, ct)) return null;

        int length = BitConverter.ToInt32(header, 0);
        if (length < 0 || length > LargestFrame)
            throw new InvalidDataException($"A speech frame claimed to be {length} bytes.");

        byte[] payload = new byte[length];
        if (length > 0 && !await FillAsync(stream, payload, ct)) return null;

        return ((Kind)header[4], payload);
    }

    /// <summary>Writes one frame whole, so two writers cannot interleave halves of two messages.</summary>
    internal static async Task WriteAsync(
        Stream stream, Kind kind, byte[] payload, CancellationToken ct = default)
    {
        byte[] frame = new byte[5 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), payload.Length);
        frame[4] = (byte)kind;
        payload.CopyTo(frame, 5);

        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(at), ct);
            if (read == 0) return false;
            at += read;
        }

        return true;
    }

    // The payload encodings. Deliberately hand-written rather than serialised: there are five of them,
    // they never travel between versions (the worker is this same binary, started by this process),
    // and audio as a length-prefixed byte array costs one copy where a serialiser would cost several.

    internal static byte[] Ready(bool canHear, bool canSpeak, string detail)
    {
        byte[] text = Encoding.UTF8.GetBytes(detail);
        byte[] payload = new byte[2 + text.Length];
        payload[0] = canHear ? (byte)1 : (byte)0;
        payload[1] = canSpeak ? (byte)1 : (byte)0;
        text.CopyTo(payload, 2);
        return payload;
    }

    internal static (bool CanHear, bool CanSpeak, string Detail) ReadReady(byte[] payload) =>
        (payload[0] == 1, payload[1] == 1, Encoding.UTF8.GetString(payload, 2, payload.Length - 2));

    internal static byte[] Transcribe(uint id, bool ifIdle, string vocabulary, byte[] audio)
    {
        byte[] words = Encoding.UTF8.GetBytes(vocabulary);
        byte[] payload = new byte[4 + 1 + 4 + words.Length + audio.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = ifIdle ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(payload.AsSpan(5, 4), words.Length);
        words.CopyTo(payload, 9);
        audio.CopyTo(payload, 9 + words.Length);

        return payload;
    }

    internal static (uint Id, bool IfIdle, string Vocabulary, byte[] Audio) ReadTranscribe(byte[] payload)
    {
        uint id = BitConverter.ToUInt32(payload, 0);
        bool ifIdle = payload[4] == 1;
        int words = BitConverter.ToInt32(payload, 5);
        string vocabulary = Encoding.UTF8.GetString(payload, 9, words);
        byte[] audio = payload[(9 + words)..];

        return (id, ifIdle, vocabulary, audio);
    }

    internal static byte[] Transcribed(uint id, Outcome outcome, string text)
    {
        byte[] said = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[5 + said.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = (byte)outcome;
        said.CopyTo(payload, 5);

        return payload;
    }

    internal static (uint Id, Outcome Outcome, string Text) ReadTranscribed(byte[] payload) =>
        (BitConverter.ToUInt32(payload, 0),
         (Outcome)payload[4],
         Encoding.UTF8.GetString(payload, 5, payload.Length - 5));

    internal static byte[] Synthesize(uint id, string voice, string text)
    {
        byte[] named = Encoding.UTF8.GetBytes(voice);
        byte[] said = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[4 + 4 + named.Length + said.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), named.Length);
        named.CopyTo(payload, 8);
        said.CopyTo(payload, 8 + named.Length);

        return payload;
    }

    internal static (uint Id, string Voice, string Text) ReadSynthesize(byte[] payload)
    {
        uint id = BitConverter.ToUInt32(payload, 0);
        int named = BitConverter.ToInt32(payload, 4);

        return (id,
            Encoding.UTF8.GetString(payload, 8, named),
            Encoding.UTF8.GetString(payload, 8 + named, payload.Length - 8 - named));
    }

    internal static byte[] Synthesized(uint id, Outcome outcome, byte[] audio)
    {
        byte[] payload = new byte[5 + audio.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = (byte)outcome;
        audio.CopyTo(payload, 5);

        return payload;
    }

    internal static (uint Id, Outcome Outcome, byte[] Audio) ReadSynthesized(byte[] payload) =>
        (BitConverter.ToUInt32(payload, 0), (Outcome)payload[4], payload[5..]);
}
