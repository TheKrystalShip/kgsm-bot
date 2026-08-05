using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Discord;
using Discord.WebSocket;

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
    public async Task<Result> AnnounceAsync(ServerAnnouncement announcement)
    {
        if (!_options.Announce.IsEnabled(announcement.Kind))
        {
            _logger.LogDebug("Announcement {Kind} for {InstanceName} is switched off",
                announcement.Kind, announcement.InstanceName);
            return Result.Success();
        }

        try
        {
            var channelResult = await ResolveChannelAsync(announcement.InstanceName);
            if (channelResult.IsFailure)
            {
                _logger.LogWarning("Not announcing {Kind} for {InstanceName}: {Error}",
                    announcement.Kind, announcement.InstanceName, channelResult.Error);
                return Result.Failure(channelResult.Error ?? "No channel to announce in");
            }

            if (_discordClient.GetChannel(channelResult.Value) is not ITextChannel channel)
            {
                _logger.LogWarning("Could not find channel {ChannelId} for instance {InstanceName}",
                    channelResult.Value, announcement.InstanceName);
                return Result.Failure($"Could not find channel for instance {announcement.InstanceName}");
            }

            var message = await channel.SendMessageAsync(
                Render(announcement),
                allowedMentions: AllowedMentions.None);

            if (_options.DeleteStatusMessageAfterDelay)
            {
                ScheduleDeletion(message, announcement.InstanceName);
            }

            _logger.LogInformation("Announced {Kind} for instance {InstanceName}",
                announcement.Kind, announcement.InstanceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error announcing {Kind} for instance {InstanceName}",
                announcement.Kind, announcement.InstanceName);
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// The server's own channel, or the fallback announcement channel when it has none.
    /// </summary>
    /// <remarks>
    /// A server only has a channel of its own if the bot saw it installed or found it in the
    /// <c>KGSM:Instances</c> map. Anything older than that map routes nowhere, which is what the
    /// fallback is for; with no fallback configured the announcement is dropped and the reason is
    /// logged, rather than being posted somewhere it does not belong.
    /// </remarks>
    private async Task<Result<ulong>> ResolveChannelAsync(string instanceName)
    {
        var channelResult = await _channelRegistry.GetChannelIdAsync(instanceName);
        if (channelResult.IsSuccess)
        {
            return channelResult;
        }

        if (_options.AnnouncementChannelId != 0)
        {
            return Result.Success(_options.AnnouncementChannelId);
        }

        return Result.Failure<ulong>(
            $"instance {instanceName} has no channel and no fallback announcement channel is configured");
    }

    private void ScheduleDeletion(IUserMessage message, string instanceName)
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
                    _logger.LogError(ex, "Failed to delete announcement for instance {InstanceName}",
                        instanceName);
                }
            });
    }

    /// <summary>
    /// Renders the message: a marker, the server, what happened, and the detail and actor the event
    /// carried. A detail or actor the event did not carry is left out — the sentence is shorter, not
    /// padded with "unknown".
    /// </summary>
    private string Render(ServerAnnouncement announcement)
    {
        string marker = MarkerFor(announcement.Kind);
        string text = $"{marker} **{announcement.InstanceName}** {VerbFor(announcement.Kind)}";

        if (announcement.Detail is not null)
        {
            text += $" — {announcement.Detail}";
        }

        if (Attribution(announcement.Actor) is string actor)
        {
            text += $" ({actor})";
        }

        return text;
    }

    /// <summary>
    /// The status markers the operator configured, reused so one guild's chosen symbols apply
    /// everywhere. A kind with no configured marker of its own gets a literal.
    /// </summary>
    private string MarkerFor(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Started or AnnouncementKind.Ready or AnnouncementKind.Restarted
            => _options.Status.Online,
        AnnouncementKind.Stopped => _options.Status.Offline,
        AnnouncementKind.Uninstalled => _options.Status.Uninstalled,
        AnnouncementKind.Crashed => "💥",
        AnnouncementKind.Failed => "🛑",
        AnnouncementKind.Updated => "⬆️",
        AnnouncementKind.Installed => "📦",
        AnnouncementKind.BackupCreated or AnnouncementKind.BackupRestored => "💾",
        AnnouncementKind.PlayerJoined => "➡️",
        AnnouncementKind.PlayerLeft => "⬅️",
        AnnouncementKind.PlayerKicked or AnnouncementKind.PlayerBanned => "🔨",
        AnnouncementKind.PlayerUnbanned => "🕊️",
        _ => "ℹ️",
    };

    private static string VerbFor(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Started => "is starting",
        AnnouncementKind.Ready => "is ready to play",
        AnnouncementKind.Stopped => "has stopped",
        AnnouncementKind.Restarted => "was restarted",
        AnnouncementKind.Crashed => "crashed and is being restarted",
        AnnouncementKind.Failed => "is down — the supervisor gave up restarting it",
        AnnouncementKind.Updated => "was updated",
        AnnouncementKind.Installed => "was installed",
        AnnouncementKind.Uninstalled => "was uninstalled",
        AnnouncementKind.BackupCreated => "was backed up",
        AnnouncementKind.BackupRestored => "was restored from a backup",
        AnnouncementKind.PlayerJoined => "— player joined",
        AnnouncementKind.PlayerLeft => "— player left",
        AnnouncementKind.PlayerKicked => "— player kicked",
        AnnouncementKind.PlayerBanned => "— player banned",
        AnnouncementKind.PlayerUnbanned => "— player unbanned",
        _ => kind.ToString(),
    };

    /// <summary>
    /// How to credit the event's actor, or null to credit nobody.
    /// </summary>
    /// <remarks>
    /// The actor is carried verbatim from the engine and is not always a person: the supervisor
    /// acting on its own emits <c>system:watchdog</c>, and crediting that to "someone" would be a
    /// fabricated identity. A system actor is named as the system; a human actor keeps whatever
    /// string the engine recorded, prefix and all, because re-deriving a surface from it would be a
    /// second answer that could disagree with the audit trail.
    /// </remarks>
    private static string? Attribution(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) return null;

        string trimmed = actor.Trim();
        if (trimmed.StartsWith("system", StringComparison.OrdinalIgnoreCase))
        {
            return "automatic";
        }

        return $"by {trimmed}";
    }
}
