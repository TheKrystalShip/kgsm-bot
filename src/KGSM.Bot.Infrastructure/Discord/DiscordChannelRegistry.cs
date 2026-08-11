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
    private readonly IDiscordSendQueue _queue;
    private readonly DiscordOptions _discordOptions;
    private readonly ILogger<DiscordChannelRegistry> _logger;

    public DiscordChannelRegistry(
        DiscordSocketClient discordClient,
        IGuildStore guilds,
        IDiscordSendQueue queue,
        IOptions<DiscordOptions> discordOptions,
        ILogger<DiscordChannelRegistry> logger)
    {
        _discordClient = discordClient;
        _guilds = guilds;
        _queue = queue;
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

            // A guild that does not follow this server gets no channel for it. Making one would be
            // the loudest possible way to ignore the filter: a channel in somebody's sidebar, named
            // after a game they said they did not want to hear about.
            if (!_guilds.Follows(topology.GuildId, instanceName))
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

            // Background lane: an install is not a burst, and the channel is wanted before the next
            // event about that server rather than this instant. Channel creation is also the most
            // expensive thing the bot asks Discord for, so it queues behind what people are reading.
            Result<ITextChannel> created = await _queue.SendAsync(
                $"create a channel for {instanceName} in guild {topology.GuildId}",
                SendLane.Background,
                async () => (ITextChannel)await guild.CreateTextChannelAsync(instanceName, properties =>
                {
                    properties.CategoryId = category.Id;
                }));

            if (created.IsFailure)
                return created;

            ITextChannel channel = created.Value!;

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

        // Every configured guild, not only the ones with a board and not only the ones that follow
        // this server: a guild that turned its board off, or stopped following a server it used to,
        // still holds the binding it made while it did — and a binding to a channel for a server that
        // no longer exists is exactly what has to go.
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
                string name = channel.Name;

                Result deleted = await _queue.SendAsync(
                    $"delete the channel for {instanceName} in guild {guildId}",
                    SendLane.Background,
                    () => channel.DeleteAsync());

                // The binding stays when the channel does. Unbinding a channel that is still there
                // orphans it — full of that server's history, and nothing pointing at it.
                if (deleted.IsFailure)
                    return deleted;

                _logger.LogInformation(
                    "Deleted channel {ChannelName} for instance {InstanceName} in guild {GuildId}",
                    name, instanceName, guildId);
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
