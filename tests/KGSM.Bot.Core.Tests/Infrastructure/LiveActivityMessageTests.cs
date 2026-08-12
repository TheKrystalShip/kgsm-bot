using Discord;

using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The one message that narrates a turn: what it renders, and what it refuses to let a failure cost.
/// </summary>
public sealed class LiveActivityMessageTests
{
    private readonly IMessageChannel _channel = Substitute.For<IMessageChannel>();
    private readonly IDiscordSendQueue _queue = Substitute.For<IDiscordSendQueue>();

    private LiveActivityMessage Board() =>
        new(_channel, _queue, "🔍 Working…", "narrate a turn", NullLogger.Instance);

    private static AssistantActivity Step(
        string id, string tool, string label, string? subject = null, string? detail = null,
        AssistantActivityState state = AssistantActivityState.Done) =>
        new(id, tool, label, subject, detail, state);

    [Fact]
    public void ItOpensWithTheHeadlineAndNothingElse() =>
        Board().Render().Should().Be("🔍 Working…");

    [Fact]
    public void AStepInFlight_ReadsAsInFlight()
    {
        var board = Board();
        board.Report(Step("1", "read_console", "Read the console", "triageprobe",
            state: AssistantActivityState.Running));

        board.Render().Should().Be("🔍 Working…\n⏳ Read the console — triageprobe");
    }

    /// <summary>
    /// A step is one row from start to finish. Keyed by the assistant's own id, so a finish replaces
    /// its own start rather than adding a second row for the same call.
    /// </summary>
    [Fact]
    public void AFinishedStep_ReplacesItsOwnStart()
    {
        var board = Board();
        board.Report(Step("1", "read_console", "Read the console", "triageprobe",
            state: AssistantActivityState.Running));
        board.Report(Step("1", "read_console", "Read the console", "triageprobe", "exit 137"));

        board.Render().Should().Be("🔍 Working…\n✓ Read the console — triageprobe (exit 137)");
    }

    /// <summary>
    /// Two reads of two different servers are two rows. Keyed on the tool name alone they would
    /// collapse into one, and the account of the turn would under-report what it did.
    /// </summary>
    [Fact]
    public void TwoCallsOfOneTool_AreTwoRows()
    {
        var board = Board();
        board.Report(Step("1", "read_console", "Read the console", "alpha"));
        board.Report(Step("2", "read_console", "Read the console", "beta"));

        board.Render().Should().Contain("alpha").And.Contain("beta");
        board.Steps.Should().HaveCount(2);
    }

    /// <summary>
    /// A runaway turn is still one readable message, and the count of what is not shown is printed —
    /// a truncated list presented as a complete one is the thing this must not look like.
    /// </summary>
    [Fact]
    public void ARunawayTurn_SaysHowMuchItIsNotShowing()
    {
        var board = Board();
        for (var i = 0; i < 20; i++)
            board.Report(Step(i.ToString(), "events", "Read recent events", $"server{i}"));

        var rendered = board.Render();
        rendered.Should().Contain("8 earlier steps");
        rendered.Should().Contain("server19");
        rendered.Should().NotContain("server0 ");
    }

    /// <summary>
    /// Nothing here quotes a tool. The rendered message is built from labels, subjects and the short
    /// description of a card — never from a tool's own output, which for a console read is the
    /// server's log and carries the address of everyone who connected.
    /// </summary>
    [Fact]
    public void ItRendersWhatWasConsulted_NeverWhatWasRead()
    {
        var board = Board();
        board.Report(Step("1", "read_console", "Read the console", "triageprobe", "exit 137"));

        var rendered = board.Render();
        rendered.Should().NotContain("91.64");
        rendered.Should().Be("🔍 Working…\n✓ Read the console — triageprobe (exit 137)");
    }

    /// <summary>
    /// A message that was never posted — Discord refused, or was unreachable — reports that it did not
    /// carry the answer, so the caller posts it itself. Claiming otherwise would leave the one thing
    /// somebody is waiting for existing nowhere.
    /// </summary>
    [Fact]
    public async Task WithNoMessagePosted_TheAnswerIsReportedAsNotCarried()
    {
        var board = Board();

        await board.StartAsync();

        (await board.FinishAsync("✅ Done", "the findings")).Should().BeFalse();
    }

    /// <summary>
    /// Failing to narrate never throws into the turn. It is called from inside the work's own try, so
    /// an exception here would abort the thing it was meant to describe.
    /// </summary>
    [Fact]
    public async Task AFailingQueue_NeverThrowsIntoTheTurn()
    {
        _queue.SendAsync(Arg.Any<string>(), Arg.Any<SendLane>(), Arg.Any<Func<Task<IUserMessage>>>())
            .Throws(new InvalidOperationException("discord is down"));

        var board = Board();

        await board.Invoking(b => b.StartAsync()).Should().NotThrowAsync();
        await board.Invoking(b => b.FinishAsync("✅ Done")).Should().NotThrowAsync();
    }
}
