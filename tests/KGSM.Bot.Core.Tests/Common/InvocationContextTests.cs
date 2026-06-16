using FluentAssertions;

using KGSM.Bot.Core.Common;

using Xunit;

namespace KGSM.Bot.Core.Tests.Common;

/// <summary>
/// The ambient provenance holder that lets a slash command / LLM turn attribute the kgsm mutation it
/// triggers (who + through-what) without threading an actor through every command and handler.
/// </summary>
public sealed class InvocationContextTests
{
    [Fact]
    public void Current_IsNull_OutsideAnyScope()
    {
        var ctx = new AsyncLocalInvocationContext();
        ctx.Current.Should().BeNull();
    }

    [Fact]
    public void Begin_SetsCurrent_AndDisposeRestoresThePrevious()
    {
        var ctx = new AsyncLocalInvocationContext();

        using (ctx.Begin(new Invocation("discord:haru", "discord")))
        {
            ctx.Current.Should().NotBeNull();
            ctx.Current!.Actor.Should().Be("discord:haru");
            ctx.Current.Origin.Should().Be("discord");
        }

        ctx.Current.Should().BeNull(); // restored on dispose
    }

    [Fact]
    public void Begin_Nested_RestoresTheOuterValue()
    {
        var ctx = new AsyncLocalInvocationContext();

        using (ctx.Begin(new Invocation("discord:outer", "discord")))
        {
            using (ctx.Begin(new Invocation("discord:inner", "discord")))
                ctx.Current!.Actor.Should().Be("discord:inner");

            ctx.Current!.Actor.Should().Be("discord:outer"); // inner dispose restored the outer
        }
    }

    [Fact]
    public async Task Current_FlowsAcrossAnAwait()
    {
        var ctx = new AsyncLocalInvocationContext();

        using (ctx.Begin(new Invocation("discord:haru", "discord")))
        {
            await Task.Yield();
            ctx.Current!.Actor.Should().Be("discord:haru"); // AsyncLocal flows down the async chain
        }
    }

    [Theory]
    [InlineData("haru", "discord:haru")]
    [InlineData("Some User", "discord:Some User")]
    public void ForDiscordUser_BuildsProviderPrefixedActor_WithDiscordOrigin(string username, string expectedActor)
    {
        Invocation inv = Invocation.ForDiscordUser(username);
        inv.Actor.Should().Be(expectedActor);
        inv.Origin.Should().Be(Invocation.DiscordOrigin);
        inv.Origin.Should().Be("discord");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForDiscordUser_BlankUsername_YieldsNullActor_NeverFabricated(string? username)
    {
        Invocation inv = Invocation.ForDiscordUser(username);
        inv.Actor.Should().BeNull();         // KGSM keeps its OS-user fallback
        inv.Origin.Should().Be("discord");   // the surface is still honestly known
    }
}
