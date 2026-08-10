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
/// Implementation of <see cref="IDiscordChannelRegistry"/> over the guild store.
/// </summary>
/// <remarks>
/// <b>Every binding it makes is written to the store</b>, not held in memory: a channel created on
/// install has to still be that server's channel after a restart, or the channel holding its history
/// is orphaned and a new one is made beside it on the next event.
/// </remarks>
public class DiscordChannelRegistry : IDiscordChannelRegistry
{
    private readonly DiscordSocketClient _discordClient;
    private readonly IGuildStore _guilds;
    private readonly DiscordOptions _discordOptions;
    private readonly ILogger<DiscordChannelRegistry> _logger;

    public DiscordChannelRegistry(
        DiscordSocketClient discordClient,
        IGuildStore guilds,
        IOptions<DiscordOptions> discordOptions,
        ILogger<DiscordChannelRegistry> logger)
    {
        _discordClient = discordClient;
        _guilds = guilds;
        _discordOptions = discordOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> AddOrUpdateChannelAsync(string instanceName)
    {
        List<string> failures = [];

        foreach (GuildTopology topology in _guilds.Configured())
        {
            if (topology.BoardCategoryId is not ulong categoryId)
                continue;

            Result result = await AddOrUpdateInAsync(topology, categoryId, instanceName);
            if (result.IsFailure)
                failures.Add($"{topology.GuildId}: {result.Error}");
        }

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(string.Join("; ", failures));
    }

    private async Task<Result> AddOrUpdateInAsync(GuildTopology topology, ulong categoryId, string instanceName)
    {
        try
        {
            SocketGuild? guild = _discordClient.GetGuild(topology.GuildId);
            if (guild is null)
                return Result.Failure("the bot cannot see this guild");

            // Already bound and still there — the server keeps the channel holding its history.
            if (_guilds.ChannelFor(topology.GuildId, instanceName) is ulong bound
                && guild.GetTextChannel(bound) is not null)
            {
                return Result.Success();
            }

            // Checked before creating rather than after failing: a guild that revoked the permission
            // says so once here, instead of throwing on every install.
            if (!guild.CurrentUser.GuildPermissions.ManageChannels)
                return Result.Failure("the bot no longer has Manage Channels here");

            SocketCategoryChannel? category = guild.GetCategoryChannel(categoryId);
            if (category is null)
                return Result.Failure($"the configured category {categoryId} is gone");

            ITextChannel channel = await guild.CreateTextChannelAsync(instanceName, properties =>
            {
                properties.CategoryId = category.Id;
            });

            _logger.LogInformation(
                "Created channel {ChannelName} for instance {InstanceName} in guild {GuildId}",
                channel.Name, instanceName, topology.GuildId);

            return _guilds.BindChannel(topology.GuildId, instanceName, channel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding a channel for instance {InstanceName} in guild {GuildId}",
                instanceName, topology.GuildId);
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> RemoveChannelAsync(string instanceName)
    {
        List<string> failures = [];

        // Every configured guild, not only the ones with a board: a guild that turned its board off
        // still holds the bindings it made while it was on, and those are what have to go.
        foreach (GuildTopology topology in _guilds.Configured())
        {
            if (_guilds.ChannelFor(topology.GuildId, instanceName) is not ulong channelId)
                continue;

            Result result = await RemoveFromAsync(topology.GuildId, channelId, instanceName);
            if (result.IsFailure)
                failures.Add($"{topology.GuildId}: {result.Error}");
        }

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(string.Join("; ", failures));
    }

    private async Task<Result> RemoveFromAsync(ulong guildId, ulong channelId, string instanceName)
    {
        try
        {
            // The binding goes whether or not the channel does. Keeping it would leave the next
            // server of the same name posting into a channel about a server that no longer exists.
            if (!_discordOptions.RemoveChannelOnInstanceDeletion)
                return _guilds.UnbindChannel(guildId, instanceName);

            SocketGuild? guild = _discordClient.GetGuild(guildId);
            if (guild is null)
                return Result.Failure("the bot cannot see this guild");

            if (guild.GetTextChannel(channelId) is SocketTextChannel channel)
            {
                await channel.DeleteAsync();
                _logger.LogInformation(
                    "Deleted channel {ChannelName} for instance {InstanceName} in guild {GuildId}",
                    channel.Name, instanceName, guildId);
            }
            else
            {
                _logger.LogWarning("Channel {ChannelId} for instance {InstanceName} is already gone",
                    channelId, instanceName);
            }

            return _guilds.UnbindChannel(guildId, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing the channel for instance {InstanceName} in guild {GuildId}",
                instanceName, guildId);
            return Result.Failure(ex.Message);
        }
    }
}
