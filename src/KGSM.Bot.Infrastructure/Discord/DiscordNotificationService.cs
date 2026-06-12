using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Discord;
using Discord.WebSocket;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace KGSM.Bot.Infrastructure.Discord;

/// <summary>
/// Implementation of IDiscordNotificationService
/// </summary>
public class DiscordNotificationService : IDiscordNotificationService
{
    private readonly DiscordSocketClient _discordClient;
    private readonly IDiscordChannelRegistry _channelRegistry;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(
        DiscordSocketClient discordClient,
        IDiscordChannelRegistry channelRegistry,
        IOptions<DiscordOptions> options,
        ILogger<DiscordNotificationService> logger)
    {
        _discordClient = discordClient;
        _channelRegistry = channelRegistry;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> NotifyRunningStatusUpdatedAsync(string instanceName, InstanceStatus status)
    {
        try
        {
            _logger.LogInformation("Notifying about status update for instance {InstanceName}: {Status}",
                instanceName, status);

            // Get channel ID for the instance
            var channelResult = await _channelRegistry.GetChannelIdAsync(instanceName);
            if (channelResult.IsFailure)
            {
                _logger.LogWarning("Failed to get channel ID for instance {InstanceName}: {Error}",
                    instanceName, channelResult.Error);
                return Result.Failure(channelResult.Error ?? "Failed to get channel ID");
            }

            // Find the channel
            if (_discordClient.GetChannel(channelResult.Value) is not ITextChannel channel)
            {
                _logger.LogWarning("Could not find channel for instance {InstanceName} with ID {ChannelId}",
                    instanceName, channelResult.Value);
                return Result.Failure($"Could not find channel for instance {instanceName}");
            }

            // Get status message based on status
            string statusMessage = GetStatusMessage(status);
            if (string.IsNullOrEmpty(statusMessage))
            {
                _logger.LogWarning("Status message is empty for status {Status}", status);
                statusMessage = $"Status: {status}";
            }

            // Send notification
            var message = await channel.SendMessageAsync(statusMessage);

            // If configured, delete the message after a delay
            if (_options.DeleteStatusMessageAfterDelay && status != InstanceStatus.Inactive)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(_options.DeleteStatusMessageDelaySeconds))
                    .ContinueWith(async _ =>
                    {
                        try
                        {
                            await message.DeleteAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to delete status message for instance {InstanceName}",
                                instanceName);
                        }
                    });
            }

            _logger.LogInformation("Successfully notified about status update for instance {InstanceName}",
                instanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying about status update for instance {InstanceName}",
                instanceName);
            return Result.Failure(ex.Message);
        }
    }

    private string GetStatusMessage(InstanceStatus status)
    {
        return status switch
        {
            InstanceStatus.Active => _options.Status.Online,
            InstanceStatus.Inactive => _options.Status.Offline,
            _ => $"Status: {status}"
        };
    }
}
