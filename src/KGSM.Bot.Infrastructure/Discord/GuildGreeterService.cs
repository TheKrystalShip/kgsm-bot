using Discord;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Discord;

/// <inheritdoc cref="IGuildGreeter" />
/// <remarks>
/// <para>
/// <b>Somewhere it can post, or nowhere.</b> The system channel first, because that is the channel a
/// guild has already nominated for exactly this kind of message; then the first channel the bot can
/// actually see and speak in; then the owner's DMs. Each step is checked rather than attempted —
/// three failed sends to find a channel is three requests spent on a guild that may never be
/// configured. A guild where none of them works is left alone and logged, which is the honest end of
/// it: there is nowhere to say anything.
/// </para>
/// <para>
/// <b>A guild that is already set up is not greeted.</b> Re-adding a bot to a guild that already has
/// a row is a reconnection, not an introduction, and it is already working.
/// </para>
/// </remarks>
public sealed class GuildGreeterService : IGuildGreeter
{
    private readonly DiscordSocketClient _discordClient;
    private readonly IGuildStore _guilds;
    private readonly IDiscordSendQueue _queue;
    private readonly ILogger<GuildGreeterService> _logger;

    private bool _watching;

    public GuildGreeterService(
        DiscordSocketClient discordClient,
        IGuildStore guilds,
        IDiscordSendQueue queue,
        ILogger<GuildGreeterService> logger)
    {
        _discordClient = discordClient;
        _guilds = guilds;
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_watching)
            return;

        _watching = true;
        _discordClient.JoinedGuild += OnJoinedGuildAsync;
    }

    private async Task OnJoinedGuildAsync(SocketGuild guild)
    {
        try
        {
            _logger.LogInformation("Added to Discord server {GuildName} ({GuildId}).", guild.Name, guild.Id);

            if (_guilds.Find(guild.Id) is not null)
            {
                _logger.LogInformation(
                    "Guild {GuildId} is already set up, so it is announcing already and needs no " +
                    "introduction.", guild.Id);
                return;
            }

            if (await SomewhereAsync(guild) is not IMessageChannel channel)
            {
                _logger.LogWarning(
                    "Added to guild {GuildId} with nowhere to post — an admin there will have to run " +
                    "/setup with no prompting from me.", guild.Id);
                return;
            }

            // Background lane: this is correct whenever it lands, and a guild joining is never the
            // traffic that causes a throttle — but it is still an unprompted send, so it is paced with
            // everything else rather than made straight off the client.
            Result sent = await _queue.SendAsync(
                $"introduce myself in guild {guild.Id}",
                SendLane.Background,
                () => channel.SendMessageAsync(embed: Greeting(), allowedMentions: AllowedMentions.None));

            if (sent.IsFailure)
            {
                _logger.LogWarning("Could not introduce myself in guild {GuildId}: {Reason}",
                    guild.Id, sent.Error);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not introduce myself in guild {GuildId}.", guild.Id);
        }
    }

    /// <summary>
    /// The best place to say this, or null when there is nowhere.
    /// </summary>
    private async Task<IMessageChannel?> SomewhereAsync(SocketGuild guild)
    {
        if (guild.SystemChannel is SocketTextChannel system && CanPostIn(guild, system))
            return system;

        if (guild.TextChannels
                .OrderBy(c => c.Position)
                .FirstOrDefault(c => CanPostIn(guild, c)) is SocketTextChannel first)
        {
            return first;
        }

        if (guild.Owner is null)
            return null;

        // The owner is the one account guaranteed to be able to act on this, and a DM is the only
        // surface left when every channel is closed to the bot. Opening it can be refused outright —
        // a closed DM is a real answer — and that is the end of the attempt rather than an error.
        try
        {
            return await guild.Owner.CreateDMChannelAsync();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "The owner of guild {GuildId} does not take DMs.", guild.Id);
            return null;
        }
    }

    private static bool CanPostIn(SocketGuild guild, SocketTextChannel channel)
    {
        ChannelPermissions permissions = guild.CurrentUser.GetPermissions(channel);
        return permissions is { ViewChannel: true, SendMessages: true, EmbedLinks: true };
    }

    /// <summary>
    /// What the bot says, once. It explains the silence, names the one command that ends it, and says
    /// who can run it — because "run /setup" from a bot that then refuses you is worse than nothing.
    /// </summary>
    private static Embed Greeting() =>
        new EmbedBuilder()
            .WithTitle("KGSM is here, and quiet on purpose")
            .WithColor(Color.DarkTeal)
            .WithDescription(
                "I manage game servers, and I won't say anything in this Discord server until " +
                "somebody tells me to. That's deliberate — a host doesn't get to start broadcasting " +
                "into a server just because someone added a bot.")
            .AddField("To start",
                "An admin runs `/setup announce` with the channel this server should hear about game " +
                "servers in. That one command is a working setup.")
            .AddField("Then, optionally",
                "`/setup follow` — hear about only the servers you care about\n" +
                "`/setup status` — keep one message always showing what's up\n" +
                "`/setup board` — give each game server its own channel")
            .AddField("Who can do it",
                "Whoever holds **admin** on the KGSM host — a Discord role grants nothing here. " +
                "Everyone else can already use the read commands: try `/list` or `/players`.")
            .Build();
}
