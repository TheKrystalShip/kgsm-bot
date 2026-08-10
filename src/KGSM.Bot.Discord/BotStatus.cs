using System.Text.Json.Serialization;

namespace KGSM.Bot.Discord;

/// <summary>
/// The bot's one-line status snapshot: what the gateway is doing, every Discord server it is set up
/// in, the channels it holds in each, and which announcements are switched on.
/// <para>
/// The reason this exists is that systemd liveness is not health for this leaf. The unit reports active,
/// the gateway reports Connected, and the bot can still be unable to post a single message — a guild
/// having failed to populate is exactly that state, and nothing outside the process could see it. Every
/// field about a guild is read from the live Discord client rather than from the store, so a guild that
/// is configured and not resolved is the interesting one.
/// </para>
/// </summary>
/// <param name="ConnectionState">The gateway's own word: <c>Disconnected｜Connecting｜Connected｜Disconnecting</c>.</param>
/// <param name="LatencyMs">Gateway heartbeat round-trip. Null before the first heartbeat completes.</param>
/// <param name="CommandCount">Slash commands this build registers.</param>
/// <param name="StoreAvailable">Whether the guild store could be opened. False means nothing is
/// announced anywhere and <c>/setup</c> refuses, whatever else here reads healthy.</param>
/// <param name="StoreUnavailableReason">Why not, when it could not.</param>
/// <param name="Guilds">One row per Discord server an admin has set up, in the order the store holds
/// them. <b>Empty is a real state</b>: a bot invited everywhere and set up nowhere is deliberately
/// silent, and that is a different fact from a broken one.</param>
/// <param name="Announcements">Every announcement switch and its state, in declaration order. Host
/// policy, not per-guild — what this host announces is its own, only where it lands is a guild's.</param>
/// <param name="SendQueue">What is waiting to go out to Discord.</param>
public sealed record BotStatus(
    string ConnectionState,
    int? LatencyMs,
    int CommandCount,
    bool StoreAvailable,
    string? StoreUnavailableReason,
    IReadOnlyList<BotGuild> Guilds,
    IReadOnlyList<BotSwitch> Announcements,
    BotSendQueue SendQueue);

/// <summary>
/// The outbound queue's backlog — the one thing here that says the bot is falling behind rather than
/// failing.
/// </summary>
/// <remarks>
/// Connected, configured, every channel visible, and messages arriving minutes late is a real state
/// and the only symptom is a depth that does not come back down. A gateway that reads healthy says
/// nothing about it, which is why it is on this line and not derived from anything else here.
/// </remarks>
/// <param name="Announcements">Announcements waiting to be sent.</param>
/// <param name="Background">Housekeeping waiting to be sent: board edits, pins, channel management.</param>
/// <param name="BackingOff">The queue is holding off after a rate limit or a Discord server error.
/// Sustained, this is the throttle that would otherwise take the whole surface down with it.</param>
public sealed record BotSendQueue(int Announcements, int Background, bool BackingOff);

/// <summary>
/// One Discord server this host announces into: what it was set up with, and what the bot can
/// actually do there.
/// </summary>
/// <remarks>
/// The two are separate answers on purpose. A recorded category the bot can no longer create channels
/// under, and a recorded channel it can no longer see, both look perfectly configured from the store
/// alone — and both mean silence.
/// </remarks>
/// <param name="GuildId">The configured guild id, as a string (a ulong exceeds what JSON numbers carry safely).</param>
/// <param name="Name">The guild as the client holds it. <b>Null while the client has not populated
/// it</b>, which is the failure this endpoint exists to expose: configured, connected, no guild.</param>
/// <param name="MemberCount">Members in the resolved guild, or null when it isn't resolved.</param>
/// <param name="AnnounceChannelId">Where this guild hears about a server with no channel of its own.</param>
/// <param name="AnnounceChannelName">That channel's name as the client sees it, or null when it can't resolve it.</param>
/// <param name="AnnounceChannelVisible">The client can currently resolve that channel. False is a
/// guild that will silently receive nothing.</param>
/// <param name="BoardCategoryId">The category per-server channels are made under, or null when this
/// guild takes everything in the one channel.</param>
/// <param name="CanManageChannels">Whether the bot still holds Manage Channels here. False with a
/// board configured means no new server will get a channel.</param>
/// <param name="ConfiguredBy">The KGSM account that ran <c>/setup</c>.</param>
/// <param name="Channels">The server→channel bindings in this guild, each saying whether the client
/// can currently see that channel.</param>
public sealed record BotGuild(
    string GuildId,
    string? Name,
    int? MemberCount,
    string AnnounceChannelId,
    string? AnnounceChannelName,
    bool AnnounceChannelVisible,
    string? BoardCategoryId,
    bool CanManageChannels,
    string ConfiguredBy,
    IReadOnlyList<BotChannel> Channels);

/// <summary>
/// One server→channel binding. <see cref="Visible"/> is the load-bearing field: a channel id that is
/// recorded but that the client cannot resolve is a message that will silently never arrive, and it
/// looks identical to a working one everywhere else.
/// </summary>
/// <param name="Instance">The server instance name, as kgsm knows it.</param>
/// <param name="ChannelId">The bound channel id, as a string.</param>
/// <param name="ChannelName">The channel's name as the client sees it, or null when it can't resolve it.</param>
/// <param name="Visible">The client can currently resolve this channel and post to it.</param>
public sealed record BotChannel(
    string Instance,
    string ChannelId,
    string? ChannelName,
    bool Visible);

/// <summary>One announcement switch: the key the Control Panel edits it by, its label, and its state.</summary>
public sealed record BotSwitch(string Key, string Label, bool Enabled);

/// <summary>
/// Source-generated JSON for the status line. The bot is single-file JIT rather than AOT, so reflection
/// would work — this is source-generated anyway to keep the wire shape declared in one place and to match
/// how every other leaf in the ecosystem serializes its socket payload.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BotStatus))]
public sealed partial class BotStatusJsonContext : JsonSerializerContext;
