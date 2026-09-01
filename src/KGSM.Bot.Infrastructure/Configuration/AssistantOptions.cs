using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.ComponentConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Where the kgsm-assistant leaf is, and the secret that lets this bot speak for a person to it.
/// </summary>
/// <remarks>
/// The assistant is optional here, as every leaf is to every other: with no address configured the
/// bot's slash commands, announcements and channel status all work exactly as they do now, and only
/// the @-mention surface is unavailable.
/// </remarks>
[ConfigSection(Section)]
public class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>
    /// Base address of the assistant's HTTP surface. Blank leaves the conversational surface off.
    /// </summary>
    /// <panel>Where the assistant service is reached. It is normally on this same machine, on the
    /// loopback address it binds, which is what this is set to. Left blank, the bot still runs commands
    /// and announces, and only answering questions is unavailable.</panel>
    [ConfigField("assistantBaseUrl", "Assistant address", Group = "assistant", Risk = ConfigRisk.Wiring)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// How long one question may take before the bot gives up on it. Generous, because a question
    /// that makes the assistant read several servers legitimately takes a while.
    /// </summary>
    /// <panel>How long to wait for an answer before giving up. A question that has the assistant check
    /// several servers takes longer than a simple one.</panel>
    [ConfigField("assistantTimeoutSec", "Answer timeout", Group = "assistant",
        Type = ConfigType.Int, Min = 1, Unit = "s")]
    public int TimeoutSeconds { get; set; } = 300;
}
