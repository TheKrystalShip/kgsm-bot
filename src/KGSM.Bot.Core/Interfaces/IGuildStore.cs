using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Which Discord servers this host announces into, and which channel each of their game servers
/// reports in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A guild receives announcements because an admin set it up</b>, never because the bot happens to
/// be in it. A guild with a row here gets every enabled announcement; a guild with no row gets
/// nothing, whatever the bot's membership.
/// </para>
/// <para>
/// <b>Reads answer even when the store could not be opened</b> — with nothing, which is the same
/// shape as "no guild is configured" and posts nowhere. <see cref="Available"/> is what tells the
/// difference, and <c>/setup</c> refuses with <see cref="UnavailableReason"/> rather than recording
/// a configuration into a file it cannot write.
/// </para>
/// </remarks>
public interface IGuildStore
{
    /// <summary>Whether the store could be opened at all.</summary>
    bool Available { get; }

    /// <summary>Why not, when <see cref="Available"/> is <see langword="false"/>.</summary>
    string? UnavailableReason { get; }

    /// <summary>Every guild an admin has configured, ordered by id.</summary>
    IReadOnlyList<GuildTopology> Configured();

    /// <summary>What one guild is configured with, or <see langword="null"/> when it is not.</summary>
    GuildTopology? Find(ulong guildId);

    /// <summary>
    /// Set where a guild hears about servers, adding it if this is the first time. Leaves the board
    /// alone — turning it on and off is <see cref="SetBoard"/>'s job.
    /// </summary>
    Result SetAnnounceChannel(ulong guildId, ulong announceChannelId, string configuredBy);

    /// <summary>
    /// Turn the board on under <paramref name="boardCategoryId"/>, or off with
    /// <see langword="null"/>. Turning it off leaves every channel it created standing.
    /// </summary>
    Result SetBoard(ulong guildId, ulong? boardCategoryId);

    /// <summary>
    /// Drop a guild and its channel bindings. The bot goes quiet there; the channels themselves are
    /// left alone.
    /// </summary>
    Result Forget(ulong guildId);

    /// <summary>The channel a server reports in here, or <see langword="null"/> when it has none.</summary>
    ulong? ChannelFor(ulong guildId, string instance);

    /// <summary>Every server→channel binding in one guild, ordered by instance name.</summary>
    IReadOnlyList<GuildChannel> ChannelsIn(ulong guildId);

    /// <summary>Record the channel a server reports in, replacing any binding it already had.</summary>
    Result BindChannel(ulong guildId, string instance, ulong channelId);

    /// <summary>Forget a server's channel here. The channel itself is not touched.</summary>
    Result UnbindChannel(ulong guildId, string instance);
}
