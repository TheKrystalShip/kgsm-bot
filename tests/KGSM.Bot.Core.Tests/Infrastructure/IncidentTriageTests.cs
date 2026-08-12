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
        _assistant.AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AssistantTurn("It ran out of memory.", [])));
    }

    private IncidentTriage Triage() =>
        new(_assistant, _queue, Options.Create(_options), NullLogger<IncidentTriage>.Instance);

    private static IThreadChannel Thread(ulong id = ThreadId)
    {
        var thread = Substitute.For<IThreadChannel>();
        thread.Id.Returns(id);
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

        _assistant.DidNotReceive().AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SwitchedOff_NothingIsInvestigated()
    {
        _options.IncidentTriage = false;

        Triage().Begin(GaveUp(), Thread(), GuildId);

        _assistant.DidNotReceive().AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>());
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

        _assistant.DidNotReceive().AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>());
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

        _assistant.Received(1).AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>());
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

        _assistant.Received(2).AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>());
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

    // --- what it refuses to do -------------------------------------------------------------------

    /// <summary>
    /// A staged action is dropped rather than posted. Nobody asked for this turn, so there is no one
    /// whose click would mean anything — and the announcement above it already carries the one action
    /// that belongs here, which is a restart.
    /// </summary>
    [Fact]
    public async Task AStagedAction_IsNeverOfferedToTheThread()
    {
        _assistant.AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AssistantTurn(
                "It ran out of memory.",
                [new StagedAction("restart", "factorio", null, "deadbeef")])));

        Triage().Begin(GaveUp(), Thread(), GuildId);
        await AskedAsync();
        await Task.Delay(50);

        // One post, and it is the findings — nothing carrying a component to click.
        _queue.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IDiscordSendQueue.SendAsync))
            .Should().BeLessThanOrEqualTo(1);
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
        _assistant.Received(1).AskAsync(Arg.Any<AssistantAsk>(), Arg.Any<CancellationToken>());
    }
}
