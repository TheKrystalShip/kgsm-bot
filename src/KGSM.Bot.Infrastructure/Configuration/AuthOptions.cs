using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.Auth.Users;
using TheKrystalShip.KGSM.ComponentConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Where this bot reads the cluster's authority, and which node it is on, from the files the node on
/// this machine keeps.
/// </summary>
/// <remarks>
/// Both are the node's, read and never written: the replica is the one every member on the machine
/// evaluates a person against, so a person holds the same access whichever surface they reach KGSM
/// through. Read straight off disk rather than asked for over HTTP, so the bot keeps authorizing people
/// with every other leaf stopped.
/// </remarks>
[ConfigSection(Section)]
public class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// The node's authority replica. Its directory must be readable by the user this unit runs as.
    /// </summary>
    /// <panel>The file the node on this machine keeps its copy of the cluster's accounts and roles in.
    /// Someone's Discord account decides who they are; the roles their account holds decide what they
    /// may do here.</panel>
    [ConfigField("authUsersDbPath", "Authority replica", Group = "authorization", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string UsersDbPath { get; set; } = UserStoreOptions.DefaultPath;

    /// <summary>
    /// The host file the node on this machine writes its member id into, which is the node a grant
    /// names to reach this bot's commands.
    /// </summary>
    /// <panel>Where this bot reads which node it is on. The node on this machine writes it; leave it at
    /// the default unless the node writes somewhere else.</panel>
    [ConfigField("authProviderFile", "Node identity file", Group = "authorization", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string ProviderFilePath { get; set; } = HostProviderFile.DefaultPath;
}
