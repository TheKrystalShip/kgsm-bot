using Discord;
using Discord.Interactions;

using Microsoft.Extensions.Logging;

using System.Reflection;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Discord module for general commands
/// </summary>
[RequireTier(KgsmTier.Viewer)]
public class GeneralModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ILogger<GeneralModule> _logger;

    public GeneralModule(ILogger<GeneralModule> logger)
    {
        _logger = logger;
    }

    [SlashCommand("ping", "Check if the bot is responsive")]
    public async Task PingAsync()
    {
        try
        {
            _logger.LogInformation("Handling ping command");

            var latency = Context.Client.Latency;

            var embed = new EmbedBuilder()
                .WithTitle("🏓 Pong!")
                .WithDescription($"Bot is online. Latency: {latency}ms")
                .WithColor(Color.Green)
                .WithCurrentTimestamp()
                .Build();

            await RespondAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling ping command");
            await RespondAsync($"An error occurred: {ex.Message}");
        }
    }

    [SlashCommand("about", "Show information about the bot")]
    public async Task AboutAsync()
    {
        try
        {
            _logger.LogInformation("Handling about command");

            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "Unknown";

            var embed = new EmbedBuilder()
                .WithTitle("KGSM Bot")
                .WithDescription("A Discord bot for managing game servers via KGSM.")
                .WithColor(Color.Blue)
                .WithCurrentTimestamp()
                .AddField("Version", version, true)
                .AddField("GitHub", "[TheKrystalShip/KGSM-Bot](https://github.com/TheKrystalShip/KGSM-Bot)", true)
                .AddField("KGSM", "[TheKrystalShip/KGSM](https://github.com/TheKrystalShip/KGSM)", true)
                .Build();

            await RespondAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling about command");
            await RespondAsync($"An error occurred: {ex.Message}");
        }
    }
}
