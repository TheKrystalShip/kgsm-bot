using Discord;

using FluentAssertions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.KGSM.Auth;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// The investigation the assistant runs into a server the supervisor gave up on: when it runs at
/// all, what it is allowed to do, and what it refuses to turn into an action.
/// </summary>
public sealed class IncidentTriageTests
{
    private const ulong GuildId = 4242;
    private const ulong ThreadId = 9001;

    private readonly IAssistantTurnClient _assistant = Substitute.For<IAssistantTurnClient>();
    private readonly IDiscordSendQueue _queue = Substitute.For<IDiscordSendQueue>();
    private readonly DiscordOptions _options = new();

    public IncidentTriageTests()
    {
        _assistant.IsConfigured.Returns(true);
        _assistant.AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AssistantTurn("It ran out of memory.", [])));
    }

    private IncidentTriage Triage() =>
        new(_assistant, _queue, Options.Create(_options), NullLogger<IncidentTriage>.Instance);

    private static IThreadChannel Thread(ulong id = ThreadId, IDisposable? typing = null)
    {
        var thread = Substitute.For<IThreadChannel>();
        thread.Id.Returns(id);
        thread.EnterTypingState().Returns(typing ?? Substitute.For<IDisposable>());
        return thread;
    }

    private static ServerAnnouncement GaveUp(string instance = "factorio") =>
        new(AnnouncementKind.Failed, instance, "exit 137, after 5", "system:watchdog");

    /// <summary>Waits for the detached investigation to reach the assistant.</summary>
    private async Task<AssistantAsk?> AskedAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            var call = _assistant.ReceivedCalls()
                .FirstOrDefault(c => c.GetMethodInfo().Name == nameof(IAssistantTurnClient.AskAsync));
            if (call is not null)
                return (AssistantAsk)call.GetArguments()[0]!;
            await Task.Delay(10);
        }

        return null;
    }

    // --- when it runs ----------------------------------------------------------------------------

    /// <summary>
    /// A give-up is the announcement worth investigating: the supervisor has stopped, the server stays
    /// down until a person acts, and everything explaining why is on the host unread.
    /// </summary>
    [Fact]
    public async Task AGiveUp_IsInvestigated()
    {
        Triage().Begin(GaveUp(), Thread(), GuildId);

        (await AskedAsync()).Should().NotBeNull();
    }

    /// <summary>
    /// A crash mid-streak is not. The supervisor is already restarting it, so the server is likely up
    /// again before the investigation finishes — and one report per attempt during a crash loop is a
    /// thread full of writing about a problem that is still happening.
    /// </summary>
    [Fact]
    public void AnOrdinaryCrash_IsNotInvestigated()
    {
        Triage().Begin(new ServerAnnouncement(AnnouncementKind.Crashed, "factorio", "exit 1, attempt 2"),
            Thread(), GuildId);

        _assistant.DidNotReceive().AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SwitchedOff_NothingIsInvestigated()
    {
        _options.IncidentTriage = false;

        Triage().Begin(GaveUp(), Thread(), GuildId);

        _assistant.DidNotReceive().AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// No assistant on this host leaves the give-up announced exactly as it is without one. This is
    /// the leaf-independence rule at its narrowest: an absent sibling costs the enhancement, never the
    /// thing the bot is for.
    /// </summary>
    [Fact]
    public void WithNoAssistantConfigured_NothingIsInvestigated()
    {
        _assistant.IsConfigured.Returns(false);

        Triage().Begin(GaveUp(), Thread(), GuildId);

        _assistant.DidNotReceive().AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The same server going down again in the same guild is left alone for a while. A server
    /// restarted into the same wall repeatedly would otherwise spend a turn on each identical
    /// investigation.
    /// </summary>
    [Fact]
    public async Task TheSameServerTwice_IsInvestigatedOnce()
    {
        var triage = Triage();

        triage.Begin(GaveUp(), Thread(), GuildId);
        await AskedAsync();
        triage.Begin(GaveUp(), Thread(9002), GuildId);
        await Task.Delay(50);

        _assistant.Received(1).AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The cooldown is per guild, not per host. One give-up opens a thread in every guild that follows
    /// the server, and each of those threads is a different set of people owed the findings.
    /// </summary>
    [Fact]
    public async Task TheSameGiveUp_IsInvestigatedInEachGuildThatHeardIt()
    {
        var triage = Triage();

        triage.Begin(GaveUp(), Thread(), GuildId);
        triage.Begin(GaveUp(), Thread(9002), GuildId + 1);

        for (var i = 0; i < 100 && _assistant.ReceivedCalls().Count(
            c => c.GetMethodInfo().Name == nameof(IAssistantTurnClient.AskAsync)) < 2; i++)
            await Task.Delay(10);

        _assistant.Received(2).AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>());
    }

    // --- what it is allowed to do ----------------------------------------------------------------

    /// <summary>
    /// Asked as the thread's room, so this becomes the opening turn of the conversation everyone in
    /// the thread continues — rather than a wall of text beside a conversation that starts cold.
    /// </summary>
    [Fact]
    public async Task ItAsksAsTheThreadsRoom()
    {
        Triage().Begin(GaveUp(), Thread(), GuildId);

        (await AskedAsync())!.Room.Should().Be($"{GuildId}-{ThreadId}");
    }

    /// <summary>
    /// Operator, because the console of the run that died is an authorized read and it is the one
    /// artifact that explains a crash. At viewer this would report on a server whose logs it could not
    /// open.
    /// </summary>
    [Fact]
    public async Task ItAsksAtOperator_SoItCanReadTheConsole()
    {
        Triage().Begin(GaveUp(), Thread(), GuildId);

        (await AskedAsync())!.Tier.Should().Be(KgsmTier.Operator);
    }

    /// <summary>
    /// It asks as itself, not as a person. Nobody asked for this turn, and attributing it to a human
    /// would put words in the mouth of whoever happened to be around.
    /// </summary>
    [Fact]
    public async Task ItAsksAsItself_NotAsAnybody()
    {
        Triage().Begin(GaveUp(), Thread(), GuildId);

        var ask = (await AskedAsync())!;
        ask.UserId.Should().NotBe(string.Empty);
        ask.UserId.Should().NotMatchRegex(@"^\d+$", "a snowflake-shaped id would read as a Discord user");
        ask.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>The prompt names the server and carries what the event actually said about it.</summary>
    [Fact]
    public async Task ThePromptNamesTheServerAndWhatWasMeasured()
    {
        Triage().Begin(GaveUp("terraria"), Thread(), GuildId);

        var prompt = (await AskedAsync())!.Prompt;
        prompt.Should().Contain("terraria");
        prompt.Should().Contain("exit 137, after 5");
    }

    /// <summary>
    /// The thread shows the bot typing while the investigation runs — the same indicator, from the
    /// same call, that somebody gets when they @-mention it. An investigation takes as long as it
    /// takes, and a thread that opens silent looks like a thread nothing is coming to.
    /// </summary>
    [Fact]
    public async Task ItTypesInTheThreadWhileItWorks()
    {
        var typing = Substitute.For<IDisposable>();
        var thread = Thread(typing: typing);

        Triage().Begin(GaveUp(), thread, GuildId);
        await AskedAsync();

        thread.Received(1).EnterTypingState();
    }

    /// <summary>
    /// And it stops when the answer does. Discord.Net keeps re-sending the indicator until the state
    /// is disposed, so a turn that ended without disposing would leave the thread typing forever.
    /// </summary>
    [Fact]
    public async Task ItStopsTypingWhenTheAnswerArrives()
    {
        var typing = Substitute.For<IDisposable>();
        var thread = Thread(typing: typing);

        Triage().Begin(GaveUp(), thread, GuildId);
        await AskedAsync();

        for (var i = 0; i < 100 && typing.ReceivedCalls().Count() == 0; i++)
            await Task.Delay(10);

        typing.Received(1).Dispose();
    }

    /// <summary>
    /// A failed turn stops typing too. The thread is about to be told the investigation could not
    /// run, and an indicator still going underneath that says one is still coming.
    /// </summary>
    [Fact]
    public async Task ItStopsTypingWhenTheInvestigationFails()
    {
        _assistant.AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AssistantTurn>("the assistant is unreachable"));

        var typing = Substitute.For<IDisposable>();
        var thread = Thread(typing: typing);

        Triage().Begin(GaveUp(), thread, GuildId);
        await AskedAsync();

        for (var i = 0; i < 100 && typing.ReceivedCalls().Count() == 0; i++)
            await Task.Delay(10);

        typing.Received(1).Dispose();
    }

    /// <summary>
    /// A thread the bot cannot type in still gets the findings. The indicator is decoration on the
    /// investigation, not the investigation — and it is started from inside the turn's own try, where
    /// an unhandled refusal would abort the whole thing and post nothing at all.
    /// </summary>
    [Fact]
    public async Task IfItCannotType_TheInvestigationStillHappens()
    {
        var thread = Substitute.For<IThreadChannel>();
        thread.Id.Returns(ThreadId);
        thread.EnterTypingState().Returns(_ => throw new InvalidOperationException("missing access"));

        Triage().Begin(GaveUp(), thread, GuildId);

        (await AskedAsync()).Should().NotBeNull();
    }

    /// <summary>
    /// Nothing types for an announcement that is never investigated — a thread showing the bot typing
    /// about a crash it decided not to look into is a promise of a message that never comes.
    /// </summary>
    [Fact]
    public void AnAnnouncementItSkips_NeverTypes()
    {
        var thread = Thread();

        Triage().Begin(new ServerAnnouncement(AnnouncementKind.Crashed, "factorio", "exit 1, attempt 2"),
            thread, GuildId);

        thread.DidNotReceive().EnterTypingState();
    }

    // --- what it refuses to do -------------------------------------------------------------------

    /// <summary>
    /// A staged action is dropped rather than posted. Nobody asked for this turn, so there is no one
    /// whose click would mean anything — and the announcement above it already carries the one action
    /// that belongs here, which is a restart.
    /// </summary>
    /// <remarks>
    /// Asserted as "it changes nothing": a turn that stages three actions reaches Discord exactly as
    /// often as one that stages none. A count that grew with the staged actions would be this surface
    /// having rendered them.
    /// </remarks>
    [Fact]
    public async Task AStagedAction_ChangesNothingThatReachesTheThread()
    {
        var withNone = await SendsForTurnAsync(new AssistantTurn("It ran out of memory.", []));
        var withThree = await SendsForTurnAsync(new AssistantTurn(
            "It ran out of memory.",
            [
                new StagedAction("restart", "factorio", null, "aaaa"),
                new StagedAction("stop", "factorio", null, "bbbb"),
                new StagedAction("backup", "factorio", null, "cccc"),
            ]));

        withThree.Should().Be(withNone);
    }

    /// <summary>How many times one investigation reaches Discord, for the turn the assistant returns.</summary>
    private static async Task<int> SendsForTurnAsync(AssistantTurn turn)
    {
        var assistant = Substitute.For<IAssistantTurnClient>();
        assistant.IsConfigured.Returns(true);
        assistant.AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(turn));

        var queue = Substitute.For<IDiscordSendQueue>();
        var triage = new IncidentTriage(
            assistant, queue, Options.Create(new DiscordOptions()), NullLogger<IncidentTriage>.Instance);

        triage.Begin(GaveUp(), Thread(), GuildId);

        for (var i = 0; i < 100; i++)
        {
            var asked = assistant.ReceivedCalls()
                .Any(c => c.GetMethodInfo().Name == nameof(IAssistantTurnClient.AskAsync));
            if (asked)
                break;
            await Task.Delay(10);
        }

        // Let the posts that follow the answer settle before counting them.
        await Task.Delay(150);
        return queue.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IDiscordSendQueue.SendAsync));
    }

    /// <summary>
    /// This surface never asks for auto-run, and a turn nobody is watching is the last place it would
    /// be safe: an investigation that restarted a server by itself would be indistinguishable from one
    /// that only looked at it.
    /// </summary>
    [Fact]
    public async Task ItNeverAsksToActWithoutAHuman()
    {
        Triage().Begin(GaveUp(), Thread(), GuildId);

        // Auto-run is pinned off by the client for every caller; what this asserts is that triage does
        // not reach for a way around it — it asks through the same path every human question uses.
        (await AskedAsync()).Should().NotBeNull();
        _assistant.Received(1).AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<IProgress<AssistantActivity>>(), Arg.Any<CancellationToken>());
    }
}
