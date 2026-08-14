namespace KGSM.Bot.Core.Voice;

/// <summary>
/// Watches whether the encrypted voice session is still carrying anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>A DAVE session can fail totally and report nothing.</b> Measured on this host: one speaker's
/// stream failed to decrypt at fifty packets a second for minutes on end while the connection stayed
/// up, the gateway stayed Connected, and no error was raised. Every frame was dropped before reaching
/// the recogniser, so the surface went silent and looked exactly like a bot that had stopped
/// understanding people. The documented baseline for this is a few percent of frames; this is the
/// same failure at a hundred.
/// </para>
/// <para>
/// <b>Both halves are needed to call it.</b> Failures alone are normal — a handful always occur while
/// the group re-keys — and no frames alone is just a quiet room. It is failures arriving <em>and</em>
/// nothing getting through that means the keys are wrong rather than the channel being silent.
/// </para>
/// <para>
/// <b>Windows, not totals.</b> A session that lost a burst of frames an hour ago and has worked ever
/// since is healthy, and a running total would convict it forever.
/// </para>
/// </remarks>
public sealed class VoiceDecryptHealth
{
    /// <summary>How long to watch before deciding.</summary>
    /// <remarks>
    /// Long enough that a re-key's burst of failures is followed by real frames inside the same
    /// window, and short enough that a person is not left talking to a dead session for long.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many failures in a window are too many to be a re-key.
    /// </summary>
    /// <remarks>
    /// Voice runs at fifty packets a second per speaker, so a truly broken stream produces about five
    /// hundred in a window and a transition produces a few dozen. This sits well above the noise and
    /// far below the failure.
    /// </remarks>
    private const int TooManyFailures = 150;

    private readonly object _gate = new();
    private DateTimeOffset _windowOpened;
    private int _failed;
    private int _received;

    /// <summary>A frame could not be decrypted.</summary>
    public void Failed()
    {
        lock (_gate) _failed++;
    }

    /// <summary>A frame arrived intact.</summary>
    public void Received()
    {
        lock (_gate) _received++;
    }

    /// <summary>Forgets everything — for a session that has just been re-established.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _failed = _received = 0;
            _windowOpened = default;
        }
    }

    /// <summary>
    /// Whether the session is failing wholesale. Closes the window and starts a new one when one is
    /// due, so this is safe to call at tick rate.
    /// </summary>
    public bool IsBroken(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_windowOpened == default)
            {
                _windowOpened = now;
                return false;
            }

            if (now - _windowOpened < Window) return false;

            bool broken = _failed >= TooManyFailures && _received == 0;

            _windowOpened = now;
            _failed = _received = 0;

            return broken;
        }
    }
}
