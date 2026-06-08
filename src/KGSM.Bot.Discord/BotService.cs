using KGSM.Bot.Application.Services;
using KGSM.Bot.Infrastructure.Configuration;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Discord;

/// <summary>
/// Main bot service that manages the Discord client
/// </summary>
public class BotService : BackgroundService
{
    private readonly DiscordSocketClient _discordClient;
    private readonly InteractionHandler _interactionHandler;
    private readonly MessageHandler _messageHandler;
    private readonly ServerEventCoordinatorService _serverEventCoordinator;
    private readonly DiscordOptions _discordOptions;
    private readonly ILogger<BotService> _logger;

    public BotService(
        DiscordSocketClient discordClient,
        InteractionHandler interactionHandler,
        MessageHandler messageHandler,
        ServerEventCoordinatorService serverEventCoordinator,
        IOptions<DiscordOptions> discordOptions,
        ILogger<BotService> logger)
    {
        _discordClient = discordClient;
        _interactionHandler = interactionHandler;
        _messageHandler = messageHandler;
        _serverEventCoordinator = serverEventCoordinator;
        _discordOptions = discordOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting KGSM Bot service");

            // Add Discord client logging
            _discordClient.Log += OnDiscordClientLogAsync;

            // Add handlers for Discord client events
            _discordClient.Ready += OnDiscordClientReadyAsync;

            // Initialize the interaction handler
            await _interactionHandler.InitializeAsync();

            // Initialize the message handler (LLM bridge)
            await _messageHandler.InitializeAsync();

            // Log in and start the Discord client
            await _discordClient.LoginAsync(TokenType.Bot, _discordOptions.Token);
            await _discordClient.StartAsync();

            // Wait indefinitely (until the service is stopped)
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown, ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in KGSM Bot service");
        }
        finally
        {
            // Ensure proper cleanup
            if (_discordClient.ConnectionState != ConnectionState.Disconnected)
            {
                await _discordClient.StopAsync();
                await _discordClient.LogoutAsync();
            }

            _logger.LogInformation("KGSM Bot service stopped");
        }
    }

    private Task OnDiscordClientLogAsync(LogMessage log)
    {
        var logLevel = log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };

        _logger.Log(logLevel, log.Exception, "{Source}: {Message}", log.Source, log.Message);

        return Task.CompletedTask;
    }

    private async Task OnDiscordClientReadyAsync()
    {
        try
        {
            _logger.LogInformation("Discord client is ready");

            // Set bot activity
            await _discordClient.SetActivityAsync(new Game("over servers 👀", ActivityType.Watching));

            // Initialize event coordinator
            _serverEventCoordinator.Initialize(_discordOptions.GuildId);

            _logger.LogInformation("KGSM Bot fully initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Discord client ready handler");
        }
    }
}
