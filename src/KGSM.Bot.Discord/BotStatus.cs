using System.Text.Json.Serialization;

namespace KGSM.Bot.Discord;

/// <summary>
/// The bot's one-line status snapshot: what the gateway is doing, which guild it actually resolved, the
/// channels it is holding, and which announcements are switched on.
/// <para>
/// The reason this exists is that systemd liveness is not health for this leaf. The unit reports active,
/// the gateway reports Connected, and the bot can still be unable to post a single message — the guild
/// having failed to populate is exactly that state, and nothing outside the process could see it. Every
/// field here is read from the live Discord client, never from configuration, so a value that disagrees
/// with the settings is the interesting one.
/// </para>
/// </summary>
/// <param name="ConnectionState">The gateway's own word: <c>Disconnected｜Connecting｜Connected｜Disconnecting</c>.</param>
/// <param name="LatencyMs">Gateway heartbeat round-trip. Null before the first heartbeat completes.</param>
/// <param name="GuildConfigured">The guild id the bot was configured with, as a string (a ulong exceeds
/// what JSON numbers carry safely). Null when nothing was configured.</param>
/// <param name="GuildResolved">The guild the client actually holds — its name. <b>Null while the client
/// has not populated it</b>, which is the failure this endpoint exists to expose: configured, connected,
/// and no guild.</param>
/// <param name="GuildMemberCount">Members in the resolved guild, or null when it isn't resolved.</param>
/// <param name="CommandCount">Slash commands this build registers.</param>
/// <param name="Channels">The instance→channel map the bot posts through, one row per configured
/// instance, each saying whether the client can currently see that channel.</param>
/// <param name="Announcements">Every announcement switch and its state, in declaration order.</param>
public sealed record BotStatus(
    string ConnectionState,
    int? LatencyMs,
    string? GuildConfigured,
    string? GuildResolved,
    int? GuildMemberCount,
    int CommandCount,
    IReadOnlyList<BotChannel> Channels,
    IReadOnlyList<BotSwitch> Announcements);

/// <summary>
/// One instance→channel mapping. <see cref="Visible"/> is the load-bearing field: a channel id that is
/// configured but that the client cannot resolve is a message that will silently never arrive, and it
/// looks identical to a working one everywhere else.
/// </summary>
/// <param name="Instance">The server instance name, as the settings map keys it.</param>
/// <param name="ChannelId">The configured channel id, as a string.</param>
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
