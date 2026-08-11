using FluentAssertions;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Commands;

using Xunit;

namespace KGSM.Bot.Core.Tests.Discord;

/// <summary>
/// How a day on this host reads back, and the properties that keep it truthful.
/// </summary>
/// <remarks>
/// The renderer's job is to lose nothing. The engine emits far more event types than the bot
/// announces — a measured day here carries deploy phases, UPnP forwards and prune results with no
/// announcement kind behind any of them — so the interesting cases are the ones nobody wrote a phrase
/// for.
/// </remarks>
public sealed class HistoryModuleTests
{
    private static HistoryMoment Moment(
        string type, string? instance = "factorio", string? actor = null, string? detail = null,
        DateTimeOffset? at = null) =>
        new(at ?? DateTimeOffset.UnixEpoch, type, instance, actor, detail);

    [Fact]
    public void ANamedTypeReadsAsItsPhrase()
    {
        HistoryModule.Describe("instance_crashed").Phrase.Should().Contain("crashed");
        HistoryModule.Describe("instance_backup_created").Phrase.Should().Be("was backed up");
    }

    /// <summary>
    /// A phrase that ends in a preposition takes its detail directly; anything else takes it as an
    /// aside. Joining them the same way reads as a typo on every line that event produces — which on
    /// a day with a settings save or a busy server is most of them.
    /// </summary>
    [Fact]
    public void ADetailThatFinishesTheSentenceIsNotSeparatedFromIt()
    {
        HistoryModule.Line(Moment("instance_player_joined", instance: "Ketchup", detail: "Void"))
            .Should().Contain("**Ketchup** was joined by Void").And.NotContain("by — Void");

        HistoryModule.Line(Moment("instance_config_changed", detail: "backup_time"))
            .Should().Contain("had a setting changed — backup_time");
    }

    /// <summary>
    /// An unrecognised type takes the separator, because nothing is known about how its payload reads
    /// and a separator is the one join that cannot produce a broken sentence.
    /// </summary>
    [Fact]
    public void AnUnnamedTypeTakesItsDetailAsAnAside()
    {
        HistoryModule.Describe("instance_some_future_thing").Completes.Should().BeFalse();
    }

    /// <summary>
    /// An instance installed from the blueprint it is named after is the common case here, and
    /// "stationeers was installed from stationeers" says the same thing twice.
    /// </summary>
    [Fact]
    public void ADetailThatOnlyRepeatsTheServersNameIsDropped()
    {
        string line = HistoryModule.Line(
            Moment("instance_installed", instance: "stationeers", detail: "stationeers"));

        line.Should().Contain("**stationeers** was installed from");
        line.Should().NotContain("from stationeers");
    }

    /// <summary>
    /// <b>The property the whole renderer rests on.</b> The phrase table is deliberately incomplete —
    /// the engine owns this vocabulary and adds to it — so an unrecognised type has to survive as the
    /// engine's own word. Dropping it would let <c>/history</c> report a quiet night on a host that
    /// spent it doing something nobody named yet.
    /// </summary>
    [Fact]
    public void AnUnnamedTypeIsRenderedFromTheEnginesOwnWord()
    {
        HistoryModule.Describe("instance_deploy_finished").Phrase.Should().Be("deploy finished");
        HistoryModule.Describe("instance_some_future_thing").Phrase.Should().Be("some future thing");

        // A type with no instance prefix keeps its whole word rather than losing a leading segment.
        HistoryModule.Describe("host_rebooted").Phrase.Should().Be("host rebooted");
    }

    [Fact]
    public void AnUnnamedTypeStillNamesItsServerAndActor()
    {
        string line = HistoryModule.Line(Moment("instance_deploy_finished", actor: "system:scheduler"));

        line.Should().Contain("**factorio**").And.Contain("deploy finished").And.Contain("system:scheduler");
    }

    /// <summary>
    /// Each part is dropped when the event did not carry it. A placeholder in an actor's place is a
    /// claim about who did something, which is the one thing the journal is careful never to make up.
    /// </summary>
    [Fact]
    public void WhatTheEventDidNotCarryIsNotFilledIn()
    {
        string line = HistoryModule.Line(Moment("instance_started", instance: null, actor: null, detail: null));

        line.Should().NotContain("unknown").And.NotContain("null").And.NotContain("—").And.NotContain("·");
        line.Should().Contain("started");
    }

    [Fact]
    public void TheDetailAndTheActorAreShownWhenTheEventCarriedThem()
    {
        string line = HistoryModule.Line(
            Moment("instance_config_changed", actor: "discord:heisen", detail: "memory_cap_mb"));

        line.Should().Contain("memory_cap_mb").And.Contain("discord:heisen");
    }

    /// <summary>
    /// The timestamp is Discord's own relative marker, so it renders in the reader's timezone rather
    /// than in whichever one the host happens to be set to.
    /// </summary>
    [Fact]
    public void TheTimeIsLeftForDiscordToRenderLocally()
    {
        var at = new DateTimeOffset(2026, 8, 10, 23, 28, 17, TimeSpan.Zero);

        HistoryModule.Line(Moment("instance_started", at: at))
            .Should().StartWith($"<t:{at.ToUnixTimeSeconds()}:R>");
    }

    /// <summary>
    /// A busy window must not produce an embed Discord refuses — and the count of what was shown is
    /// what lets the footer say how much was left out instead of implying it was all of it.
    /// </summary>
    [Fact]
    public void TheListIsCappedAndSaysHowMuchOfItWasShown()
    {
        List<HistoryMoment> moments = [.. Enumerable.Range(0, 500).Select(_ => Moment("instance_started"))];

        (string text, int shown) = HistoryModule.Fit(moments);

        shown.Should().BeLessThan(moments.Count);
        shown.Should().Be(text.Split('\n').Length);
        text.Length.Should().BeLessThan(4096, "Discord refuses a longer embed description outright");
    }

    /// <summary>
    /// A single event with an unreasonably long detail must not be able to push the description over
    /// the limit — the budget is counted per line as it is added, not assumed from the line count.
    /// </summary>
    [Fact]
    public void OneEnormousLineDoesNotOverflowTheBudget()
    {
        List<HistoryMoment> moments =
            [.. Enumerable.Range(0, 10).Select(_ => Moment("instance_config_changed", detail: new string('x', 900)))];

        (string text, int shown) = HistoryModule.Fit(moments);

        text.Length.Should().BeLessThan(4096);
        shown.Should().BeGreaterThan(0).And.BeLessThan(moments.Count);
    }

    [Fact]
    public void AnEmptyWindowRendersToNothingRatherThanFailing()
    {
        (string text, int shown) = HistoryModule.Fit([]);

        text.Should().BeEmpty();
        shown.Should().Be(0);
    }
}
