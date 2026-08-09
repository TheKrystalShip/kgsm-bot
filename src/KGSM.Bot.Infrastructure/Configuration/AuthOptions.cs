using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Where this host keeps the KGSM accounts the bot authorizes against.
/// </summary>
/// <remarks>
/// One file, shared with the Control Panel API and the assistant, so a person holds the same tier
/// whichever surface they reach KGSM through — each reads the record rather than deriving one. Read
/// straight off disk rather than asked for over HTTP: a file cannot be down, so the bot keeps
/// authorizing people with every other leaf stopped.
/// </remarks>
[LeafSection(Section)]
public class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// The account store. Its directory must be readable by the user this unit runs as.
    /// </summary>
    /// <panel>The file this host keeps its KGSM accounts in, shared with the Control Panel and the
    /// assistant. Someone's Discord account decides who they are; the account it is connected to
    /// decides what they may do here.</panel>
    [LeafField("authUsersDbPath", "Account store", Group = "authorization", Risk = LeafRisk.Wiring)]
    public string UsersDbPath { get; set; } = UserStoreOptions.DefaultPath;
}
