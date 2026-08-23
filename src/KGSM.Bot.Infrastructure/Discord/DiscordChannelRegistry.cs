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
    private readonly IServerLabels _labels;
    private readonly DiscordOptions _discordOptions;
    private readonly ILogger<DiscordChannelRegistry> _logger;

    public DiscordChannelRegistry(
        DiscordSocketClient discordClient,
        IGuildStore guilds,
        IDiscordSendQueue queue,
        IServerLabels labels,
        IOptions<DiscordOptions> discordOptions,
        ILogger<DiscordChannelRegistry> logger)
    {
        _discordClient = discordClient;
        _guilds = guilds;
        _queue = queue;
        _labels = labels;
        _discordOptions = discordOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> AddOrUpdateChannelAsync(string instanceName)
    {
        List<string> failures = [];

        // Read once for the host: every guild's channel says the same thing about the same server.
        string topic = TopicFor(instanceName, await _labels.LabelAsync(instanceName));

        foreach (GuildTopology topology in _guilds.Configured())
        {
            if (topology.BoardCategoryId is not ulong categoryId)
                continue;

            // A guild that does not follow this server gets no channel for it. Making one would be
            // the loudest possible way to ignore the filter: a channel in somebody's sidebar, named
            // after a game they said they did not want to hear about.
            if (!_guilds.Follows(topology.GuildId, instanceName))
                continue;

            Result result = await AddOrUpdateInAsync(topology, categoryId, instanceName, topic);
            if (result.IsFailure)
                failures.Add($"{topology.GuildId}: {result.Error}");
        }

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(string.Join("; ", failures));
    }

    /// <inheritdoc />
    public async Task<Result> RefreshChannelTopicAsync(string instanceName)
    {
        List<string> failures = [];
        string topic = TopicFor(instanceName, await _labels.LabelAsync(instanceName));

        foreach (GuildTopology topology in _guilds.Configured())
        {
            if (_guilds.ChannelFor(topology.GuildId, instanceName) is not ulong channelId)
                continue;

            if (_discordClient.GetChannel(channelId) is not ITextChannel channel)
            {
                // A channel the bot cannot see is not a channel that is gone, and this is not the
                // operation that decides which — ReconcileBindingsAsync is. A topic is decoration:
                // not writing one costs nothing.
                _logger.LogDebug(
                    "Not refreshing {InstanceName}'s channel topic in guild {GuildId}: channel " +
                    "{ChannelId} cannot be seen.",
                    instanceName, topology.GuildId, channelId);
                continue;
            }

            if (string.Equals(channel.Topic, topic, StringComparison.Ordinal))
                continue;

            // Background lane, and a channel edit is the most rate-limited thing here — which is
            // affordable only because this runs when a person renames a server, never on a state
            // change. Nothing may drive it from the lifecycle events.
            Result written = await _queue.SendAsync(
                $"set the channel topic for {instanceName} in guild {topology.GuildId}",
                SendLane.Background,
                () => channel.ModifyAsync(properties => properties.Topic = topic));

            if (written.IsFailure)
                failures.Add($"{topology.GuildId}: {written.Error}");
        }

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(string.Join("; ", failures));
    }

    /// <summary>
    /// What a server's channel says it is about: the label somebody chose, and the id every command
    /// takes. A server that was never labelled gets the id alone rather than the id twice.
    /// </summary>
    private static string TopicFor(string instanceName, string label) =>
        string.Equals(label, instanceName, StringComparison.Ordinal)
            ? $"kgsm server {instanceName}"
            : $"{label} · kgsm server {instanceName}";

    private async Task<Result> AddOrUpdateInAsync(
        GuildTopology topology, ulong categoryId, string instanceName, string topic)
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

                    // The channel is named after the id — that is what every command and binding
                    // uses, and a name cannot be kept in step with a label anyway — so the topic is
                    // where the label goes. Set here rather than edited afterwards: it rides the
                    // create call and so costs no second request.
                    properties.Topic = topic;
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

    /// <inheritdoc />
    public async Task<Result> ReconcileBindingsAsync()
    {
        List<string> failures = [];
        int unseen = 0;
        int forgotten = 0;

        foreach (GuildTopology topology in _guilds.Configured())
        {
            SocketGuild? guild = _discordClient.GetGuild(topology.GuildId);

            // A guild the bot cannot see says nothing about its bindings. Reading a missing guild as
            // "every channel in it is gone" would drop every binding a guild has over an outage.
            if (guild is null)
                continue;

            foreach (GuildChannel binding in _guilds.ChannelsIn(topology.GuildId))
            {
                if (guild.GetTextChannel(binding.ChannelId) is not null)
                    continue;

                unseen++;

                Result<bool> result = await ForgetIfGoneAsync(binding);
                if (result.IsFailure)
                    failures.Add($"{binding.Instance}: {result.Error}");
                else if (result.Value)
                    forgotten++;
            }
        }

        if (unseen > 0)
        {
            _logger.LogInformation(
                "Channel bindings: {Unseen} named a channel the bot cannot see, {Forgotten} of them " +
                "confirmed deleted and forgotten. Those servers report in their guild's announcement " +
                "channel from now on.",
                unseen, forgotten);
        }

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(string.Join("; ", failures));
    }

    /// <summary>
    /// Forget one binding, but only once Discord has confirmed the channel is really gone.
    /// </summary>
    /// <remarks>
    /// The fetch is what separates "deleted" from "the bot cannot see it": Discord answers the first
    /// with nothing and the second with a refusal, where the gateway cache gives the same silence for
    /// both. Anything other than a clean "no such channel" leaves the binding exactly where it is —
    /// this is the one operation that can orphan a channel holding a server's history, so it only acts
    /// on the answer it asked for.
    /// </remarks>
    /// <returns>Whether the binding was dropped.</returns>
    private async Task<Result<bool>> ForgetIfGoneAsync(GuildChannel binding)
    {
        IChannel? live = null;

        // Captured rather than returned: "there is no such channel" is a successful answer of null,
        // and Result<T> cannot carry one.
        Result looked = await _queue.SendAsync(
            $"check whether {binding.Instance}'s channel still exists in guild {binding.GuildId}",
            SendLane.Background,
            async () => { live = await _discordClient.Rest.GetChannelAsync(binding.ChannelId); });

        if (looked.IsFailure)
        {
            _logger.LogInformation(
                "Keeping {Instance}'s channel binding in guild {GuildId}: Discord would not say whether " +
                "channel {ChannelId} still exists ({Reason}). A channel the bot merely cannot see is " +
                "still that server's channel.",
                binding.Instance, binding.GuildId, binding.ChannelId, looked.Error);
            return Result.Failure<bool>(looked.Error ?? "the check failed");
        }

        if (live is not null)
        {
            _logger.LogDebug(
                "Keeping {Instance}'s channel binding in guild {GuildId}: channel {ChannelId} exists, " +
                "the bot just cannot see it.",
                binding.Instance, binding.GuildId, binding.ChannelId);
            return Result.Success(false);
        }

        Result forgotten = _guilds.UnbindChannel(binding.GuildId, binding.Instance);
        if (forgotten.IsFailure)
            return Result.Failure<bool>(forgotten.Error ?? "the binding could not be dropped");

        _logger.LogInformation(
            "Forgot {Instance}'s channel binding in guild {GuildId}: channel {ChannelId} was deleted.",
            binding.Instance, binding.GuildId, binding.ChannelId);

        return Result.Success(true);
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
