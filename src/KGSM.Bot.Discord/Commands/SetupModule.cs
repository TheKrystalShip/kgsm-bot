using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Discord.Autocomplete;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Configures what this Discord server hears from this KGSM host.
/// <para>
/// The bot works in any guild it is invited to and announces in none of them until an admin says so
/// here. There is no file to edit and no restart: <c>/setup announce</c> alone is a working
/// configuration, and the per-server board is the thing you deliberately turn on.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is KGSM admin, never a Discord permission.</b> Deciding where a host broadcasts —
/// including player joins and leaves — is a host setting, and host settings are admin across the
/// ecosystem. Gating on Discord's <i>Manage Server</i> instead would let anyone who can add the bot
/// to a guild of their own point this host's announcements into it, and authorizing correctly would
/// not help: an announcement has no caller to authorize.
/// </para>
/// <para>
/// <b>Two questions, two refusals.</b> <i>May you configure this host</i> is the tier, answered by
/// the precondition. <i>Can the bot actually do this here</i> is Discord's own answer, checked
/// <b>before</b> anything is recorded — recording a channel the bot cannot post in, or a category it
/// cannot create channels under, is how a guild gets configured and then silently receives nothing.
/// </para>
/// <para>
/// Not <c>[Mutating]</c>: it changes no server.
/// </para>
/// </remarks>
[Group("setup", "Configure what this Discord server hears from KGSM")]
[RequireTier(KgsmTier.Admin)]
public class SetupModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IGuildStore _guilds;
    private readonly IKgsmAccounts _accounts;
    private readonly IStatusBoard _statusBoard;
    private readonly ILogger<SetupModule> _logger;

    public SetupModule(
        IGuildStore guilds,
        IKgsmAccounts accounts,
        IStatusBoard statusBoard,
        ILogger<SetupModule> logger)
    {
        _guilds = guilds;
        _accounts = accounts;
        _statusBoard = statusBoard;
        _logger = logger;
    }

    [SlashCommand("show", "What this Discord server is set up with, and what the bot can do here")]
    public async Task ShowAsync()
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return;

        GuildTopology? topology = _guilds.Find(guild.Id);
        GuildPermissions permissions = guild.CurrentUser.GuildPermissions;

        var embed = new EmbedBuilder()
            .WithTitle("KGSM setup")
            .WithColor(topology is null ? Color.LightGrey : Color.Green)
            .WithCurrentTimestamp();

        if (topology is null)
        {
            embed.WithDescription(
                "This server hears nothing from KGSM. Run `/setup announce` with the channel it " +
                "should hear about servers in.");
        }
        else
        {
            embed.AddField("Announcements", Mention(topology.AnnounceChannelId), inline: true);
            embed.AddField("Per-server channels",
                topology.BoardCategoryId is ulong category
                    ? $"on, under <#{category}>"
                    : "off — everything goes to the one channel",
                inline: true);
            embed.AddField("Live status message",
                topology.StatusChannelId is ulong status
                    ? $"kept in <#{status}>"
                    : "off — `/setup status` picks a channel for it",
                inline: true);
            embed.AddField("Set up by",
                $"{topology.ConfiguredBy}, {topology.ConfiguredUtc:yyyy-MM-dd}", inline: true);

            IReadOnlyList<string> follows = _guilds.FollowedServers(guild.Id);
            embed.AddField("Hears about",
                follows.Count == 0
                    ? "every server on this host — `/setup follow` narrows it"
                    : string.Join(", ", follows.Select(s => $"`{s}`")));

            IReadOnlyList<GuildChannel> channels = _guilds.ChannelsIn(guild.Id);
            if (channels.Count > 0)
            {
                embed.AddField($"Servers with a channel here ({channels.Count})",
                    string.Join("\n", channels.Select(c => $"`{c.Instance}` → <#{c.ChannelId}>")));
            }
        }

        // What the bot can actually do here, which is a separate answer from what is recorded — and
        // the one that decides whether any of the above will work.
        embed.AddField("The bot can",
            $"{Mark(permissions.SendMessages)} send messages\n" +
            $"{Mark(permissions.ManageChannels)} manage channels (needed for per-server channels)\n" +
            $"{Mark(permissions.ManageMessages)} manage messages (needed to pin the status message)\n" +
            $"{Mark(permissions.CreatePublicThreads)} create threads (needed for crash threads)");

        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("announce", "Set the channel this Discord server hears about KGSM servers in")]
    public async Task AnnounceAsync(
        [Summary(description: "Channel announcements are posted in")]
        ITextChannel channel)
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return;

        // Before the write, not after: a channel the bot cannot post in, recorded, is a guild that is
        // configured and silent.
        ChannelPermissions permissions = guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.SendMessages)
        {
            await RespondAsync(
                $"🚫 I can't post in {channel.Mention} — I need **View Channel** and **Send " +
                "Messages** there. Nothing was changed.", ephemeral: true);
            return;
        }

        string account = await AccountNameAsync();
        Result result = _guilds.SetAnnounceChannel(guild.Id, channel.Id, account);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} announces in channel {ChannelId}, set by {Account}",
            guild.Id, channel.Id, account);

        await RespondAsync(
            $"✅ This server now hears about KGSM in {channel.Mention}. Turn on a channel per game " +
            "server with `/setup board`.");
    }

    [SlashCommand("board", "Give each game server its own channel, under a category")]
    public async Task BoardAsync(
        [Summary(description: "Category the per-server channels are created under")]
        ICategoryChannel category)
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return;

        if (_guilds.Find(guild.Id) is null)
        {
            await RespondAsync(
                "🚫 This server has no announcement channel yet. Run `/setup announce` first — the " +
                "one channel is what everything falls back to.", ephemeral: true);
            return;
        }

        if (!guild.CurrentUser.GuildPermissions.ManageChannels)
        {
            await RespondAsync(
                "🚫 I need **Manage Channels** in this server to make a channel per game server. " +
                "Nothing was changed — grant it and run this again, or leave the board off and keep " +
                "everything in the one channel.", ephemeral: true);
            return;
        }

        Result result = _guilds.SetBoard(guild.Id, category.Id);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} turned the board on under category {CategoryId}",
            guild.Id, category.Id);

        await RespondAsync(
            $"✅ Each game server installed from now on gets its own channel under **{category.Name}**, " +
            "and reports there instead of the announcement channel.");
    }

    [SlashCommand("board-off", "Stop making a channel per game server here")]
    public async Task BoardOffAsync()
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return;

        if (_guilds.Find(guild.Id) is null)
        {
            await RespondAsync("This server isn't set up for KGSM at all.", ephemeral: true);
            return;
        }

        Result result = _guilds.SetBoard(guild.Id, null);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} turned the board off", guild.Id);

        // Deleting channels with history because a setting changed is not a decision a bot gets to
        // make. Say so, rather than leaving somebody to wonder what happened to them.
        await RespondAsync(
            "✅ No more channels will be made here, and every server reports in the announcement " +
            "channel again. **The channels already made are left standing** — their history is yours, " +
            "not mine to delete.");
    }

    [SlashCommand("status", "Keep one message here always showing every server and how to reach it")]
    public async Task StatusAsync(
        [Summary(description: "Channel the live status message is kept in")]
        ITextChannel channel)
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return;

        if (_guilds.Find(guild.Id) is null)
        {
            await RespondAsync(
                "🚫 This server has no announcement channel yet. Run `/setup announce` first.",
                ephemeral: true);
            return;
        }

        // Before the write: a channel the bot cannot post in, recorded, is a board that never appears
        // and never says why.
        ChannelPermissions permissions = guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.SendMessages)
        {
            await RespondAsync(
                $"🚫 I can't post in {channel.Mention} — I need **View Channel** and **Send " +
                "Messages** there. Nothing was changed.", ephemeral: true);
            return;
        }

        Result result = _guilds.SetStatusChannel(guild.Id, channel.Id);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} keeps its status message in channel {ChannelId}",
            guild.Id, channel.Id);

        // Answered before the message is posted, then posted: the publish reads the whole inventory
        // and would sit past Discord's three seconds if it came first.
        await RespondAsync(
            $"✅ I'll keep one message in {channel.Mention} showing every server and how to reach it, " +
            "and edit it as things change." +
            (permissions.ManageMessages
                ? " I'll pin it, so it's easy to find."
                : " I can't pin it without **Manage Messages**, so you may want to pin it yourself."));

        await _statusBoard.PublishAsync(guild.Id);
    }

    [SlashCommand("status-off", "Stop keeping a live status message here")]
    public async Task StatusOffAsync()
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return;

        if (_guilds.Find(guild.Id) is null)
        {
            await RespondAsync("This server isn't set up for KGSM at all.", ephemeral: true);
            return;
        }

        Result result = _guilds.SetStatusChannel(guild.Id, null);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} stopped keeping a status message", guild.Id);

        // Same rule as the board: a message the bot posted is still a message in somebody's channel,
        // and deleting it because a setting changed is not a decision a bot gets to make.
        await RespondAsync(
            "✅ I'll stop updating it. **The message is left where it is** — it will simply stop " +
            "changing, so unpin or delete it whenever you like.");
    }

    // ── which servers this guild follows ──────────────────────────────────────────────────────

    [SlashCommand("follow", "Hear about only this game server here (and any others you add)")]
    public async Task FollowAsync(
        [Summary(description: "Game server to follow")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        if (await ConfiguredGuildOrRefuseAsync() is not SocketGuild guild)
            return;

        IReadOnlyList<string> before = _guilds.FollowedServers(guild.Id);

        Result result = _guilds.Follow(guild.Id, instance);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} now follows {Instance}", guild.Id, instance);

        // The first one is the one that changes the rule, and it narrows rather than widens. Somebody
        // adding a server they like should not discover by silence that they just muted fourteen more.
        await RespondAsync(before.Count == 0
            ? $"✅ This server now hears about **{instance}** only. Add more with `/setup follow`, or " +
              "go back to hearing about everything with `/setup follow-all`."
            : $"✅ This server now also hears about **{instance}**.");
    }

    [SlashCommand("unfollow", "Stop hearing about one game server here")]
    public async Task UnfollowAsync(
        [Summary(description: "Game server to stop hearing about")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        if (await ConfiguredGuildOrRefuseAsync() is not SocketGuild guild)
            return;

        IReadOnlyList<string> following = _guilds.FollowedServers(guild.Id);

        if (following.Count == 0)
        {
            await RespondAsync(
                $"This server follows everything, so there's no list to take **{instance}** out of. " +
                "Run `/setup follow` with the servers you *do* want and it'll hear about only those.",
                ephemeral: true);
            return;
        }

        if (!following.Contains(instance, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync($"This server doesn't follow **{instance}** anyway.", ephemeral: true);
            return;
        }

        // An empty list means "everything", so taking away the last one would turn a guild that hears
        // about one server into a guild that hears about all of them — the opposite of what somebody
        // unfollowing their last server is asking for. Refused, with both real choices named.
        if (following.Count == 1)
        {
            await RespondAsync(
                $"🚫 **{instance}** is the only server this Discord server follows, and an empty list " +
                "means *everything* — so this would turn the announcements back on for every server " +
                "on this host. If that's what you want, run `/setup follow-all`. If you want silence, " +
                "run `/setup forget`.",
                ephemeral: true);
            return;
        }

        Result result = _guilds.Unfollow(guild.Id, instance);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} no longer follows {Instance}", guild.Id, instance);

        await RespondAsync(
            $"✅ This server will hear nothing more about **{instance}**. " +
            "**Its channel, if it has one, is left standing** — the history in it is yours.");
    }

    [SlashCommand("follow-all", "Hear about every game server on this host again")]
    public async Task FollowAllAsync()
    {
        if (await ConfiguredGuildOrRefuseAsync() is not SocketGuild guild)
            return;

        if (_guilds.FollowedServers(guild.Id).Count == 0)
        {
            await RespondAsync("This server already hears about every server on this host.",
                ephemeral: true);
            return;
        }

        Result result = _guilds.FollowEverything(guild.Id);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} cleared its server filter", guild.Id);

        await RespondAsync("✅ This server hears about every game server on this host again.");
    }

    [SlashCommand("forget", "Stop announcing in this Discord server entirely")]
    public async Task ForgetAsync()
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return;

        if (_guilds.Find(guild.Id) is null)
        {
            await RespondAsync("This server isn't set up for KGSM at all.", ephemeral: true);
            return;
        }

        Result result = _guilds.Forget(guild.Id);
        if (result.IsFailure)
        {
            await RespondAsync($"⚠️ I couldn't record that: {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation("Guild {GuildId} was dropped from the guild store", guild.Id);

        await RespondAsync(
            "✅ I'll say nothing more in this server. **Every channel is left standing**, and slash " +
            "commands still work for anyone with a KGSM account. `/setup announce` starts it again.");
    }

    // ── shared ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The guild this was run in, having already refused unless it is set up. For the subcommands
    /// that adjust an existing configuration rather than create one.
    /// </summary>
    private async Task<SocketGuild?> ConfiguredGuildOrRefuseAsync()
    {
        if (await GuildOrRefuseAsync() is not SocketGuild guild)
            return null;

        if (_guilds.Find(guild.Id) is null)
        {
            await RespondAsync(
                "🚫 This server hears nothing from KGSM yet, so there is nothing to narrow. Run " +
                "`/setup announce` first.", ephemeral: true);
            return null;
        }

        return guild;
    }

    /// <summary>
    /// The guild this was run in, or null having already refused. Every subcommand configures a
    /// Discord server, and the commands are registered globally — so one of them can be typed in a
    /// DM, where there is nothing to configure.
    /// </summary>
    private async Task<SocketGuild?> GuildOrRefuseAsync()
    {
        if (!_guilds.Available)
        {
            await RespondAsync(
                $"⚠️ I can't read this host's setup ({_guilds.UnavailableReason}), so I won't record " +
                "one either. Nothing was changed — ask an admin to check the bot's log.",
                ephemeral: true);
            return null;
        }

        if (Context.Guild is not SocketGuild guild)
        {
            await RespondAsync(
                "This configures a Discord server, so run it in the one you want set up.",
                ephemeral: true);
            return null;
        }

        return guild;
    }

    /// <summary>
    /// The KGSM account behind the caller, recorded as who configured this guild. The precondition
    /// has already proved they hold admin; this is only reading back the name it authorized.
    /// </summary>
    private async Task<string> AccountNameAsync()
    {
        AccountAnswer answer = await _accounts.ResolveAsync(Context.User.Id);
        return answer.Account ?? Context.User.Username;
    }

    private static string Mention(ulong channelId) => $"<#{channelId}>";

    private static string Mark(bool granted) => granted ? "✅" : "❌";
}
