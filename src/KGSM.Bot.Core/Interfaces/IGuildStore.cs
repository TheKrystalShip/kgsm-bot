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
    /// Keep a live status message in <paramref name="statusChannelId"/>, or stop keeping one with
    /// <see langword="null"/>. Stopping forgets the message too; the message itself is left standing.
    /// </summary>
    Result SetStatusChannel(ulong guildId, ulong? statusChannelId);

    /// <summary>
    /// Record which message is being kept current, so a restart edits the one already posted instead
    /// of leaving it stale and posting another beside it.
    /// </summary>
    Result SetStatusMessage(ulong guildId, ulong statusMessageId);

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

    /// <summary>
    /// The servers this guild has chosen to follow, ordered by name. <b>Empty means no filter</b> —
    /// the guild follows every server on this host.
    /// </summary>
    /// <remarks>
    /// Empty is "all", not "none", and that is load-bearing in two directions. It is what a guild
    /// configured before there was a filter already has, so nothing goes silent by upgrading; and a
    /// guild wanting to hear nothing runs <c>/setup forget</c>, which is a different thing from being
    /// set up and following an empty list.
    /// </remarks>
    IReadOnlyList<string> FollowedServers(ulong guildId);

    /// <summary>Whether this guild is told about <paramref name="instance"/> at all.</summary>
    /// <remarks>
    /// True for every server when the guild has set no filter. Asked once per guild per announcement,
    /// so it is a point query rather than a list read and a comparison.
    /// </remarks>
    bool Follows(ulong guildId, string instance);

    /// <summary>
    /// Follow one server here. The first call also <i>starts</i> filtering: until there is a row, the
    /// guild follows everything, so adding the first one narrows it to that server alone.
    /// </summary>
    Result Follow(ulong guildId, string instance);

    /// <summary>Stop following one server here.</summary>
    /// <remarks>
    /// Removing the last one would empty the list, which means "all" — the opposite of what somebody
    /// unfollowing their last server intends. The caller checks for that and offers the explicit
    /// choice; this method does what it is told.
    /// </remarks>
    Result Unfollow(ulong guildId, string instance);

    /// <summary>Clear the filter, so this guild follows every server on this host again.</summary>
    Result FollowEverything(ulong guildId);
}
