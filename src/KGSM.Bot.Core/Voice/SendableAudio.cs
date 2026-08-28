namespace KGSM.Bot.Core.Voice;

/// <summary>
/// The shortest thing a Discord voice connection will actually transmit.
/// </summary>
/// <remarks>
/// <para>
/// <b>An output stream sends nothing at all until a full buffer's worth of frames is queued.</b>
/// Below that its sending loop waits for a buffer that is never going to fill, and a flush waits on
/// the sending loop — so a short write neither completes nor fails. It hangs, silently, forever.
/// </para>
/// <para>
/// <b>Measured:</b> a 290ms tone — fourteen frames against a fifty-frame buffer — wedged the stream
/// on its first use. The write never returned, the request that started it never finished, and
/// because spoken requests are answered one at a time, every later one queued behind it. The bot sat
/// in the channel hearing people perfectly and answering nobody, with no error in the log and the
/// connection reporting itself healthy.
/// </para>
/// <para>
/// <b>So short audio is padded rather than refused.</b> Trailing zeroes are silence: the sound is
/// heard at its proper moment, and what follows it costs the time it takes to drain and nothing
/// audible. Refusing instead would mean no surface could ever make a short sound, which is the wrong
/// answer to a limitation of the writer underneath.
/// </para>
/// </remarks>
public static class SendableAudio
{
    /// <summary>
    /// How much audio the writer buffers before it begins transmitting.
    /// </summary>
    /// <remarks>
    /// Declared here and passed to the stream when it is built, rather than left to the library's
    /// default, because the padding length is derived from it — the two disagreeing is precisely what
    /// wedges the stream. Deliberately generous: this is the jitter buffer between a write and the
    /// network, and a host under load has been measured stalling for seconds, which a short buffer
    /// would turn into audible gaps.
    /// </remarks>
    public const int BufferMillis = 1000;

    /// <summary>20ms of 48 kHz stereo signed 16-bit — one opus frame, the unit the writer counts in.</summary>
    public const int FrameBytes = 3840;

    /// <summary>
    /// The shortest write that will go out.
    /// </summary>
    /// <remarks>
    /// A whole frame past the count that releases the buffer. A part-frame is held back by the
    /// encoder rather than forwarded, so audio sized to the boundary exactly can arrive one frame
    /// short of the number being waited for — which is the same hang with a harder cause to see.
    /// </remarks>
    public const int PreloadBytes = (((BufferMillis + 19) / 20) + 1) * FrameBytes;

    /// <summary>The same audio, padded with silence if it is too short to be sent.</summary>
    public static byte[] AtLeastPreload(byte[] pcm)
    {
        if (pcm.Length >= PreloadBytes) return pcm;

        var padded = new byte[PreloadBytes];
        pcm.CopyTo(padded, 0);
        return padded;
    }
}
