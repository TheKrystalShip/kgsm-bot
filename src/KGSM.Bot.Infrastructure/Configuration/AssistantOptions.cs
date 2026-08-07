using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Where the kgsm-assistant leaf is, and the secret that lets this bot speak for a person to it.
/// </summary>
/// <remarks>
/// The assistant is optional here, as every leaf is to every other: with no address configured the
/// bot's slash commands, announcements and channel status all work exactly as they do now, and only
/// the @-mention surface is unavailable.
/// </remarks>
[LeafSection(Section)]
public class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>
    /// Base address of the assistant's HTTP surface. Blank leaves the conversational surface off.
    /// </summary>
    /// <panel>Where the assistant service is reached. It is normally on this same machine. Left blank,
    /// the bot still runs commands and announces, and only answering questions is unavailable.</panel>
    [LeafField("assistantBaseUrl", "Assistant address", Group = "assistant", Risk = LeafRisk.Wiring)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The host's shared relay secret, which is what lets the bot ask the assistant a question as
    /// somebody else. Without it every question is refused, so the surface is off.
    /// </summary>
    /// <remarks>
    /// The same value the assistant is configured with (<c>Assistant__Relay__Secret</c>). It
    /// authenticates the bot as a trusted relay — the identity and the authority it then forwards are
    /// the asking person's, measured from their Discord roles, never this secret's.
    /// </remarks>
    /// <panel>Shared secret the assistant recognises this bot by. It has to match the assistant's own,
    /// and without it the assistant refuses every question the bot asks on someone's behalf.</panel>
    [LeafField("assistantRelaySecret", "Assistant relay secret", Group = "assistant",
        Type = LeafType.Secret, Risk = LeafRisk.Wiring)]
    public string RelaySecret { get; set; } = string.Empty;

    /// <summary>
    /// How long one question may take before the bot gives up on it. Generous, because a question
    /// that makes the assistant read several servers legitimately takes a while.
    /// </summary>
    /// <panel>How long to wait for an answer before giving up. A question that has the assistant check
    /// several servers takes longer than a simple one.</panel>
    [LeafField("assistantTimeoutSec", "Answer timeout", Group = "assistant",
        Type = LeafType.Int, Min = 1, Unit = "s")]
    public int TimeoutSeconds { get; set; } = 300;
}
