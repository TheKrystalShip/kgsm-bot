using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Voice;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace KGSM.Bot.Core.Tests.Voice;

/// <summary>
/// An answer read out as it is written is a queue of sentences, and the two things that must hold of
/// a queue are that it plays in order and that cutting the bot off empties it.
/// </summary>
public class SpokenRecitalTests
{
    /// <summary>
    /// A recital that records what it was asked to say and can be cut off on demand — the same two
    /// things the real one offers a caller.
    /// </summary>
    private sealed class FakeRecital : IVoiceRecital
    {
        public List<string> Said { get; } = [];
        public bool Current { get; private set; } = true;
        public bool Disposed { get; private set; }

        /// <summary>Held so a test can stop the bot part-way through an answer.</summary>
        public TaskCompletionSource Playing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Hold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; }

        public void CutOff() => Current = false;

        public async Task<Result> SayAsync(byte[] pcm, CancellationToken ct = default)
        {
            lock (Said) Said.Add(FakeSpeech.Read(pcm));

            if (Block)
            {
                Playing.TrySetResult();
                await Hold.Task;
            }

            return Result.Success();
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Synthesis that carries the text through, so what was played can be read back.</summary>
    private sealed class FakeSpeech : ITextToSpeech
    {
        public bool IsAvailable => true;
        public int Calls;

        public Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<byte[]?>(System.Text.Encoding.UTF8.GetBytes(text));
        }

        public static string Read(byte[] pcm) => System.Text.Encoding.UTF8.GetString(pcm);

        public Task<(string Speaking, IReadOnlyList<string> Voices)> VoicesAsync(CancellationToken ct = default)
            => Task.FromResult<(string, IReadOnlyList<string>)>((string.Empty, []));

        public Task<Result> SpeakAsAsync(string voice, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    private static SpokenRecital Open(FakeRecital held, FakeSpeech speech, Task? after = null) =>
        new(held, speech, after ?? Task.CompletedTask, "heisen", NullLogger.Instance,
            CancellationToken.None);

    [Fact]
    public async Task SentencesArePlayedInTheOrderTheyWereWritten()
    {
        var held = new FakeRecital();
        var speech = new FakeSpeech();

        await using SpokenRecital recital = Open(held, speech);

        recital.Report("Factorio is running and has been up for eleven days. ");
        recital.Report("Terraria is stopped, and it was stopped on purpose. ");
        recital.Report("Nothing else here needs attention.");

        recital.Flush().Should().BeTrue();
        await recital.FinishAsync();

        held.Said.Should().SatisfyRespectively(
            first => first.Should().Be("Factorio is running and has been up for eleven days."),
            second => second.Should().Be("Terraria is stopped, and it was stopped on purpose."),
            third => third.Should().Be("Nothing else here needs attention."));
    }

    [Fact]
    public async Task CuttingTheBotOffDropsEverythingStillOwed()
    {
        // The trap. With five sentences queued, stopping only the one playing has the bot pause and
        // then carry on talking over the person who cut in.
        var held = new FakeRecital { Block = true };
        var speech = new FakeSpeech();

        await using SpokenRecital recital = Open(held, speech);

        recital.Report("Factorio is running and has been up for eleven days. ");
        recital.Report("Terraria is stopped, and it was stopped on purpose. ");
        recital.Report("Nothing else here needs attention right now, though. ");
        recital.Report("I would leave it alone until tomorrow morning at least.");
        recital.Flush();

        // The first sentence is in the air; the rest are queued behind it.
        await held.Playing.Task;
        held.CutOff();
        held.Hold.TrySetResult();

        await recital.FinishAsync();

        held.Said.Should().ContainSingle(
            "everything queued behind the sentence that was cut is abandoned too");
        speech.Calls.Should().Be(1, "nothing is synthesised for a recital that has been cut off");
    }

    [Fact]
    public async Task WhatThisSurfaceSaysItselfRidesTheSameRecital()
    {
        // The sentence pointing at a staged action's buttons is spoken through the recital, so cutting
        // the bot off takes it too rather than leaving it to play over whoever cut in.
        var held = new FakeRecital();
        var speech = new FakeSpeech();

        await using SpokenRecital recital = Open(held, speech);

        recital.Report("I can restart factorio for you, but I need you to confirm it first. ");
        recital.Flush().Should().BeTrue();
        recital.Say("Approve it in the chat.");

        await recital.FinishAsync();

        held.Said.Should().HaveCount(2);
        held.Said[^1].Should().Be("Approve it in the chat.");
    }

    [Fact]
    public async Task AReplyThatCarriedNoTextLeavesNothingSpoken()
    {
        // An assistant whose frames carry no reply text. The caller reads this and speaks the finished
        // answer whole, which is the shape a turn always had.
        var held = new FakeRecital();
        var speech = new FakeSpeech();

        await using SpokenRecital recital = Open(held, speech);

        recital.Flush().Should().BeFalse();
        await recital.FinishAsync();

        held.Said.Should().BeEmpty();
    }

    [Fact]
    public async Task NothingIsSaidBeforeTheAcknowledgementHasBeenHeard()
    {
        // Two things said in the same channel: the answer must not start on top of "I have it".
        var held = new FakeRecital();
        var speech = new FakeSpeech();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using SpokenRecital recital = Open(held, speech, acknowledged.Task);

        recital.Report("Factorio is running and has been up for eleven days now. ");
        recital.Flush();

        await Task.Delay(50);
        held.Said.Should().BeEmpty();

        acknowledged.TrySetResult();
        await recital.FinishAsync();

        held.Said.Should().ContainSingle();
    }

    [Fact]
    public async Task AnAcknowledgementThatFailedDoesNotSwallowTheAnswer()
    {
        var held = new FakeRecital();
        var speech = new FakeSpeech();

        await using SpokenRecital recital =
            Open(held, speech, Task.FromException(new InvalidOperationException("no card")));

        recital.Report("Factorio is running and has been up for eleven days now.");
        recital.Flush();
        await recital.FinishAsync();

        held.Said.Should().ContainSingle();
    }

    [Fact]
    public async Task TheRecitalIsClosedWhenItIsDoneWithIt()
    {
        // Left open, the next interruption would report that it had stopped something.
        var held = new FakeRecital();

        await using (SpokenRecital recital = Open(held, new FakeSpeech()))
        {
            recital.Report("Factorio is running and has been up for eleven days now.");
            recital.Flush();
            await recital.FinishAsync();
        }

        held.Disposed.Should().BeTrue();
    }
}
