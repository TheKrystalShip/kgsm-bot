using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Where the bot keeps its own record of which Discord servers it announces into.
/// </summary>
/// <remarks>
/// Its own file, not the account store: guild topology is not authority and does not belong beside
/// credentials. It sits outside <c>/opt/kgsm-bot</c> because the deploy syncs that prefix with
/// <c>rsync --delete</c>, which would take this file with it.
/// </remarks>
[LeafSection(Section)]
public class GuildOptions
{
    public const string Section = "Guilds";

    /// <summary>
    /// The store's location when nobody has configured one: the file inside the directory systemd
    /// provisions from the unit's <c>StateDirectory=kgsm-bot</c>. <see cref="StatePaths.Resolve"/>
    /// reads that directory from <c>$STATE_DIRECTORY</c> and falls back to this path.
    /// </summary>
    public const string DefaultDbPath = StatePaths.DefaultDirectory + "/bot.db";

    /// <summary>The file name inside the state directory, for <see cref="StatePaths.Resolve"/>.</summary>
    public const string DbFileName = "bot.db";

    /// <summary>
    /// The topology store. Its directory must be writable by the user this unit runs as, and must not
    /// be under the install prefix.
    /// </summary>
    /// <panel>The file the bot records each Discord server's setup in — its announcement channel and
    /// its per-server channels. Written by <c>/setup</c> in Discord, not here. Losing it loses every
    /// channel a server's history is in.</panel>
    [LeafField("guildsDbPath", "Guild store", Group = "discord", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string DbPath { get; set; } = DefaultDbPath;
}
