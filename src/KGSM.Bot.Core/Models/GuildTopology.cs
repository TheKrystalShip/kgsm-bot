namespace KGSM.Bot.Core.Models;

/// <summary>
/// What one Discord server is configured to hear from this KGSM host: where announcements go, and
/// whether the bot keeps a channel per server here.
/// </summary>
/// <remarks>
/// <b>The board is enabled by having a category, not by a boolean beside one.</b> A flag and a
/// category can disagree with each other; a nullable column cannot.
/// </remarks>
/// <param name="GuildId">The Discord server this describes.</param>
/// <param name="AnnounceChannelId">Where this guild hears about a server with no channel of its own.
/// The one required piece of configuration — a guild with this alone is a working setup.</param>
/// <param name="BoardCategoryId">The category per-server channels are created under, or
/// <see langword="null"/> when this guild takes announcements in the one channel.</param>
/// <param name="ConfiguredBy">The KGSM account that ran <c>/setup</c>.</param>
/// <param name="ConfiguredUtc">When this guild was first configured.</param>
/// <param name="UpdatedUtc">When any of it last changed.</param>
public sealed record GuildTopology(
    ulong GuildId,
    ulong AnnounceChannelId,
    ulong? BoardCategoryId,
    string ConfiguredBy,
    DateTimeOffset ConfiguredUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>Whether this guild keeps a channel per server.</summary>
    public bool HasBoard => BoardCategoryId is not null;
}

/// <summary>
/// One server's channel in one guild — the binding that ties a server to the channel holding its
/// history.
/// </summary>
/// <param name="GuildId">The guild the channel is in.</param>
/// <param name="Instance">The server instance name, as kgsm knows it.</param>
/// <param name="ChannelId">The channel this server reports in.</param>
/// <param name="CreatedUtc">When the binding was made.</param>
public sealed record GuildChannel(
    ulong GuildId,
    string Instance,
    ulong ChannelId,
    DateTimeOffset CreatedUtc);
