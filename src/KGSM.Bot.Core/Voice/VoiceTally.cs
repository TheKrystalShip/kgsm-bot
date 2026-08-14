namespace KGSM.Bot.Core.Voice;

/// <summary>
/// Counts what happens to speech on its way to being a request, so a failure can be located.
/// </summary>
/// <remarks>
/// <para>
/// <b>"It didn't hear me" has four causes and they need telling apart.</b> Nothing arrived, nothing
/// was recognised, nothing matched the trigger, or nothing could be answered — and from inside a
/// voice channel all four are the same silence. Without these numbers the only way to find out which
/// one it was is to switch on transcript logging, which writes down every private word said in the
/// room to diagnose a problem that is usually one phrase.
/// </para>
/// <para>
/// <b>Counts, not content.</b> Nothing here holds anything anybody said, which is what makes it safe
/// to have on always — and it is the reason this is worth having rather than being a weaker version of
/// the log.
/// </para>
/// <para>
/// Host-wide and since the process started, not per channel or per join. The question it answers is
/// whether recognition is working on this host, which is not a thing that varies by guild.
/// </para>
/// </remarks>
public interface IVoiceTally
{
    /// <summary>An utterance reached the recogniser.</summary>
    void Heard();

    /// <summary>The recogniser found words in it.</summary>
    void Recognised();

    /// <summary>The words were addressed to the bot, whether by trigger or as a continuation.</summary>
    void Addressed();

    /// <summary>A complete request was handed on to be answered.</summary>
    void Answered();

    /// <summary>The recogniser handed back the vocabulary it was primed with instead of speech.</summary>
    void Echoed();

    /// <summary>The counts as they stand.</summary>
    VoiceCounts Read();
}

/// <summary>
/// What became of everything heard.
/// </summary>
/// <param name="Heard">Utterances that reached the recogniser.</param>
/// <param name="Recognised">Those the recogniser found any words in.</param>
/// <param name="Addressed">Those addressed to the bot.</param>
/// <param name="Answered">Complete requests handed on.</param>
/// <param name="Echoed">Transcripts discarded as the primed vocabulary coming back.</param>
public sealed record VoiceCounts(
    long Heard, long Recognised, long Addressed, long Answered, long Echoed)
{
    /// <summary>
    /// The one-line reading of these numbers, for somebody who is not going to interpret them.
    /// </summary>
    /// <remarks>
    /// Each case is a different thing to go and do, which is the entire point of counting separately.
    /// Deliberately says nothing when the numbers are healthy: a diagnosis printed beside a working
    /// system is noise, and a diagnosis on two utterances is a guess.
    /// </remarks>
    public string? Diagnosis => this switch
    {
        { Heard: < 5 } => null,
        { Recognised: 0 } => "Nothing is being recognised — check the model is loaded and that people are audible.",
        { Addressed: 0 } => "Speech is recognised but nothing matches the trigger — switch on transcript "
                            + "logging to see how the phrase is being heard, then add that spelling.",
        { Answered: 0 } => "The bot is being addressed but no request completes — people may be pausing "
                           + "long enough to be cut off mid-sentence.",
        _ => null,
    };
}

/// <summary>The counts, kept in memory for the life of the process.</summary>
public sealed class VoiceTally : IVoiceTally
{
    private long _heard, _recognised, _addressed, _answered, _echoed;

    public void Heard() => Interlocked.Increment(ref _heard);
    public void Recognised() => Interlocked.Increment(ref _recognised);
    public void Addressed() => Interlocked.Increment(ref _addressed);
    public void Answered() => Interlocked.Increment(ref _answered);
    public void Echoed() => Interlocked.Increment(ref _echoed);

    public VoiceCounts Read() => new(
        Interlocked.Read(ref _heard),
        Interlocked.Read(ref _recognised),
        Interlocked.Read(ref _addressed),
        Interlocked.Read(ref _answered),
        Interlocked.Read(ref _echoed));
}
