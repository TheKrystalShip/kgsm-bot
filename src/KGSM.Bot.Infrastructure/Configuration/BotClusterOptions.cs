using TheKrystalShip.KGSM.ComponentConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// What this bot states about itself as a member of a cluster.
/// </summary>
/// <remarks>
/// <para>
/// The secret is deliberately absent from this class and from the settings file. It is one host-level
/// value in <c>/etc/kgsm/kgsm-cluster.env</c> that every member on the machine reads, and a second
/// place to set it is where one blank and one filled look identical from the outside.
/// </para>
/// <para>
/// Being a member is what lets this bot hold its own copy of the cluster's accounts. The auth anchor is
/// the single authority; every member is given a copy directly and answers from that, so a person is
/// identified here without asking anybody — and the assistant a turn is forwarded to resolves the same
/// person against its own copy of the same source rather than being asked to vouch for them.
/// </para>
/// </remarks>
[ConfigSection(Section)]
public sealed class BotClusterOptions
{
    public const string Section = "Cluster";

    /// <summary>
    /// This member's name in the cluster. Blank names it after the machine it runs on.
    /// </summary>
    /// <panel>What this bot is called in the cluster. Left blank it is named after the machine. Per
    /// member, never per host — a machine running a node and an assistant beside this bot holds three
    /// members and they cannot share a name.</panel>
    [ConfigField("clusterMemberId", "Member id", Group = "cluster")]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Where this bot answers the member-to-member wire.
    /// </summary>
    /// <remarks>
    /// A member is pushed to rather than polling: the anchor fans an account change out to every
    /// member's inbox, so a member with nowhere to be reached holds whatever it copied when it joined
    /// and never hears that somebody was demoted.
    /// <para>
    /// Loopback by default, which reaches an anchor on this machine and is the ecosystem's pattern —
    /// a member binds <c>127.0.0.1</c> and whatever TLS it needs is terminated in front of it. A bot
    /// deployed away from the anchor is fronted the same way and states the address it is reached at.
    /// </para>
    /// </remarks>
    /// <panel>Where this bot listens for the other members of its cluster. The default is loopback,
    /// which is right when the auth anchor runs on this machine; a bot on a machine of its own is
    /// reached through the same TLS front end every other member sits behind.</panel>
    [ConfigField("clusterUrls", "Member wire address", Group = "cluster", Risk = ConfigRisk.Wiring)]
    public string Urls { get; set; } = "http://127.0.0.1:5182";
}
