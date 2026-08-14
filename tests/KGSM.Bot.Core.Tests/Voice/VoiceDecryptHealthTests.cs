using FluentAssertions;

using KGSM.Bot.Core.Voice;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// Telling a dead encrypted session from a quiet room.
/// </summary>
/// <remarks>
/// Calling this wrong in either direction is expensive: a false positive cycles a working voice
/// connection, and a false negative is the measured failure that left somebody talking to a bot that
/// could not hear a word for three minutes.
/// </remarks>
public class VoiceDecryptHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 14, 17, 43, 0, TimeSpan.Zero);

    private readonly VoiceDecryptHealth _health = new();

    private void Fail(int times)
    {
        for (var i = 0; i < times; i++) _health.Failed();
    }

    private void Receive(int times)
    {
        for (var i = 0; i < times; i++) _health.Received();
    }

    /// <summary>Opens the first window, which no evidence can predate.</summary>
    private void Open() => _health.IsBroken(Start);

    private DateTimeOffset After(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void AQuietRoomIsNotABrokenSession()
    {
        // Nobody talking is no failures and no frames, which is most of any voice channel's life.
        Open();

        _health.IsBroken(After(30)).Should().BeFalse();
    }

    [Fact]
    public void FramesArrivingIntactIsHealthyHoweverManyAlsoFailed()
    {
        // The documented baseline is a few percent lost, and a re-key loses a burst. What matters is
        // that something is getting through.
        Open();
        Fail(400);
        Receive(50);

        _health.IsBroken(After(11)).Should().BeFalse();
    }

    [Fact]
    public void FailuresWithNothingGettingThroughIsABrokenSession()
    {
        // The measured failure: fifty packets a second, every one refused, for minutes, with the
        // connection reporting itself healthy the whole time.
        Open();
        Fail(500);

        _health.IsBroken(After(11)).Should().BeTrue();
    }

    [Fact]
    public void AHandfulOfFailuresIsATransitionNotAFailure()
    {
        Open();
        Fail(20);

        _health.IsBroken(After(11)).Should().BeFalse();
    }

    [Fact]
    public void NothingIsDecidedBeforeTheWindowIsUp()
    {
        // Judging early would convict a re-key, whose failures come first and whose frames follow.
        Open();
        Fail(500);

        _health.IsBroken(After(3)).Should().BeFalse();
    }

    [Fact]
    public void EachWindowIsJudgedOnItsOwnEvidence()
    {
        // A session that lost a burst and then recovered is working. A running total would convict
        // it for the rest of its life.
        Open();
        Fail(500);
        _health.IsBroken(After(11)).Should().BeTrue();

        Receive(50);
        _health.IsBroken(After(22)).Should().BeFalse();
    }

    [Fact]
    public void AReconnectForgetsWhatTheOldConnectionDid()
    {
        // New handshake, new keys: nothing measured about the last session says anything about this
        // one, and carrying it over would re-cycle a connection that has just been rebuilt.
        Open();
        Fail(500);

        _health.Reset();

        _health.IsBroken(After(11)).Should().BeFalse();
    }

    [Fact]
    public void CountingIsSafeFromEveryStreamAtOnce()
    {
        // Failures arrive on Discord.Net's log thread and frames on one read loop per speaker.
        Open();
        Parallel.For(0, 1000, _ => _health.Failed());
        Parallel.For(0, 1000, _ => _health.Received());

        _health.IsBroken(After(11)).Should().BeFalse("a thousand frames got through");
    }
}
