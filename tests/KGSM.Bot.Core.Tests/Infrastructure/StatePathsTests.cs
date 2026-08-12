using FluentAssertions;

using KGSM.Bot.Infrastructure.Configuration;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// Where the guild store is opened. The bot owns exactly one file and it cannot be re-derived, so
/// the rule that picks its path is worth pinning: systemd's directory is used when there is one, an
/// operator's own path always wins, and a bot run outside systemd still finds the same file.
/// </summary>
/// <remarks>
/// <c>$STATE_DIRECTORY</c> is process-wide, so these run in a collection of their own — a parallel
/// test reading it while one of these has it set would see the other's value.
/// </remarks>
[Collection(nameof(StatePathsTests))]
[CollectionDefinition(nameof(StatePathsTests), DisableParallelization = true)]
public sealed class StatePathsTests : IDisposable
{
    private const string Variable = "STATE_DIRECTORY";

    private readonly string? _saved = Environment.GetEnvironmentVariable(Variable);

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _saved);

    [Fact]
    public void Uses_the_systemd_state_directory_when_the_value_is_the_shipped_default()
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-bot");

        StatePaths.Resolve(GuildOptions.DefaultDbPath, GuildOptions.DefaultDbPath, GuildOptions.DbFileName)
            .Should().Be("/var/lib/kgsm-bot/bot.db");
    }

    [Fact]
    public void Follows_the_state_directory_wherever_the_unit_puts_it()
    {
        Environment.SetEnvironmentVariable(Variable, "/somewhere/else");

        StatePaths.Resolve(GuildOptions.DefaultDbPath, GuildOptions.DefaultDbPath, GuildOptions.DbFileName)
            .Should().Be(Path.Combine("/somewhere/else", "bot.db"));
    }

    [Fact]
    public void Takes_the_first_entry_when_the_unit_declares_several_directories()
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-bot:/var/lib/other");

        StatePaths.Directory.Should().Be("/var/lib/kgsm-bot");
    }

    [Fact]
    public void Falls_back_to_the_shipped_location_outside_systemd()
    {
        Environment.SetEnvironmentVariable(Variable, null);

        StatePaths.Directory.Should().Be(StatePaths.DefaultDirectory);
        StatePaths.Resolve(GuildOptions.DefaultDbPath, GuildOptions.DefaultDbPath, GuildOptions.DbFileName)
            .Should().Be(GuildOptions.DefaultDbPath);
    }

    [Theory]
    [InlineData("/opt/elsewhere/guilds.db")]
    [InlineData("relative.db")]
    public void A_configured_path_wins_over_the_state_directory(string configured)
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-bot");

        StatePaths.Resolve(configured, GuildOptions.DefaultDbPath, GuildOptions.DbFileName)
            .Should().Be(configured);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_is_not_a_choice_of_location(string? configured)
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-bot");

        StatePaths.Resolve(configured, GuildOptions.DefaultDbPath, GuildOptions.DbFileName)
            .Should().Be("/var/lib/kgsm-bot/bot.db");
    }
}
