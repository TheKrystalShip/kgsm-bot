using Discord;

using KGSM.Bot.Core.Models;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// How a staged action is put to somebody: the sentence and the two buttons.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every surface that can stage one — the @-mention handler and the voice channel — so a
/// restart proposed out loud and a restart proposed in a channel are offered in the same words. Two
/// surfaces wording this separately is how one of them comes to describe an uninstall gently.
/// </para>
/// <para>
/// The button carries the assistant's grant and nothing else, so the bot holds no part of the pending
/// action and a restart of either side leaves a posted button working. Only the person who asked can
/// approve it: <see cref="AssistantConfirmationModule"/> forwards whoever clicked, and the assistant
/// refuses a grant that is not theirs.
/// </para>
/// </remarks>
public static class StagedActionPrompt
{
    /// <summary>
    /// Whether a button can actually be built for this grant.
    /// </summary>
    /// <remarks>
    /// A grant too long for a Discord custom id produces a button Discord accepts and the assistant
    /// then refuses, which is worse than saying so up front.
    /// </remarks>
    public static bool CanBuild(StagedAction staged) => AssistantConfirmationIds.Fits(staged.Token);

    /// <summary>The sentence somebody reads before deciding.</summary>
    public static string Content(StagedAction staged) => staged.Kind switch
    {
        "uninstall" =>
            $"⚠️ This will **permanently delete `{staged.Target}`** and all of its data. This cannot be undone.",
        "install" =>
            $"⚙️ This will install a new **{staged.Target}** server" +
            (staged.InstanceName is null ? "" : $" named `{staged.InstanceName}`") +
            ". It can take a while.",
        "start" => $"▶️ Start **{staged.Target}**?",
        "stop" => $"⏹️ Stop **{staged.Target}**?",
        "restart" => $"🔄 Restart **{staged.Target}**?",
        "update" => $"⬆️ Update **{staged.Target}** to its latest version? It can take a while.",
        "backup" => $"💾 Back up **{staged.Target}**?",
        "setconfig" =>
            $"⚙️ Set `{staged.ConfigKey}` = `{(string.IsNullOrEmpty(staged.ConfigValue) ? "(empty)" : staged.ConfigValue)}` " +
            $"on **{staged.Target}**?",
        // A kind this bot has no wording for is still a real staged action, so it is offered rather
        // than dropped — the assistant's own reply above says what it is.
        _ => $"Confirm *{Describe(staged)}*?",
    };

    /// <summary>The action in a few words, for a sentence that is about it rather than offering it.</summary>
    public static string Describe(StagedAction staged) => staged.Kind switch
    {
        "setconfig" => $"set `{staged.ConfigKey}` on **{staged.Target}**",
        "install" => $"install **{staged.Target}**"
            + (staged.InstanceName is null ? "" : $" as `{staged.InstanceName}`"),
        _ => $"{staged.Kind} **{staged.Target}**",
    };

    /// <summary>Confirm and Cancel, carrying the grant.</summary>
    public static MessageComponent Buttons(StagedAction staged)
    {
        // Uninstalling is the one that cannot be undone, so it is the one that gets the alarming
        // button; everything else is recoverable and gets a neutral one.
        ButtonStyle style = staged.Kind == "uninstall" ? ButtonStyle.Danger : ButtonStyle.Primary;

        return new ComponentBuilder()
            .WithButton("Confirm", AssistantConfirmationIds.Confirm(staged.Token), style)
            .WithButton("Cancel", AssistantConfirmationIds.Cancel, ButtonStyle.Secondary)
            .Build();
    }

    /// <summary>What to say when the grant cannot be turned into a button.</summary>
    public static string CannotBuild(StagedAction staged) =>
        $"⚠️ I staged *{Describe(staged)}* but couldn't build a button for it — " +
        "please use the slash command instead.";
}
