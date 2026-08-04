using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration options for Discord
/// </summary>
[LeafSection(Section)]
public class DiscordOptions
{
    public const string Section = "Discord";

    /// <panel>Token the bot signs in to Discord with. Without a valid one it cannot connect at
    /// all.</panel>
    [LeafField("discordToken", "Bot token", Group = "discord", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string Token { get; set; } = string.Empty;
    /// <panel>The Discord server this bot operates in. Pointing it at another server abandons the
    /// channels it made in this one.</panel>
    [LeafField("discordGuildId", "Discord server id", Group = "discord", Type = LeafType.Int,
        Min = 0, Risk = LeafRisk.Wiring)]
    public ulong GuildId { get; set; }
    /// <panel>Category the bot creates each server's channel under. Zero leaves new channels
    /// uncategorised.</panel>
    [LeafField("instancesCategoryId", "Channel category id", Group = "channels", Type = LeafType.Int, Min = 0)]
    public ulong InstancesCategoryId { get; set; }

    /// <summary>
    /// Discord role ID whose members may trigger mutating actions (start/stop/
    /// restart/backup/update) via the LLM. Read-only queries are open to all.
    /// If 0 (unset), no one is authorized for actions until configured.
    /// </summary>
    /// <panel>Role whose holders may start, stop and otherwise act on servers from Discord — the same
    /// role the assistant checks. Zero means no one is authorized.</panel>
    [LeafField("discordActionRoleId", "Action role id", Group = "discord", Type = LeafType.Int,
        Min = 0, Risk = LeafRisk.Wiring)]
    public ulong ActionRoleId { get; set; }
    /// <panel>Whether uninstalling a server also deletes its Discord channel, taking that channel's
    /// history with it. Off, the channel is left behind.</panel>
    [LeafField("removeChannelOnUninstall", "Delete channel with the server", Group = "channels",
        Risk = LeafRisk.Destructive)]
    public bool RemoveChannelOnInstanceDeletion { get; set; } = false;
    public StatusOptions Status { get; set; } = new();
    /// <panel>Whether status messages are removed after a while, so a busy channel does not fill with
    /// them.</panel>
    [LeafField("deleteStatusMessages", "Clean up status messages", Group = "channels")]
    public bool DeleteStatusMessageAfterDelay { get; set; } = false;
    /// <panel>How long a status message stays before it is removed.</panel>
    [LeafField("deleteStatusMessagesAfterSec", "Status message lifetime", Group = "channels",
        Min = 1, Unit = "s", DependsOn = "deleteStatusMessages")]
    public int DeleteStatusMessageDelaySeconds { get; set; } = 10;
}

/// <summary>
/// Configuration options for status messages
/// </summary>
public class StatusOptions
{
    /// <panel>Shown beside a server that is running.</panel>
    [LeafField("statusOnline", "Running marker", Group = "channels")]
    public string Online { get; set; } = string.Empty;

    /// <panel>Shown beside a server that is stopped.</panel>
    [LeafField("statusOffline", "Stopped marker", Group = "channels")]
    public string Offline { get; set; } = string.Empty;

    /// <panel>Shown beside a server that is no longer installed.</panel>
    [LeafField("statusUninstalled", "Uninstalled marker", Group = "channels")]
    public string Uninstalled { get; set; } = string.Empty;
}
