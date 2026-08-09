using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Handles the Confirm/Cancel buttons posted for an action the kgsm-assistant leaf staged.
/// </summary>
/// <remarks>
/// <para>
/// The bot holds no part of the action — only the grant, which rides the button and goes straight
/// back to the assistant. The assistant decides: it re-derives the clicker's authority from the tier
/// forwarded here, re-validates the target against what exists now, and refuses a grant that belongs
/// to somebody else or has already been redeemed.
/// </para>
/// <para>
/// <b>Only the person who asked can approve.</b> A conversation belongs to one person and so do the
/// actions in it, which is the same rule the Control Panel follows — an operator cannot approve
/// another operator's proposal there either, and a surface that disagreed would be a way around it.
/// </para>
/// </remarks>
public class AssistantConfirmationModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IAssistantTurnClient _assistant;
    private readonly IKgsmAccounts _accounts;
    private readonly ILogger<AssistantConfirmationModule> _logger;

    public AssistantConfirmationModule(
        IAssistantTurnClient assistant,
        IKgsmAccounts accounts,
        ILogger<AssistantConfirmationModule> logger)
    {
        _assistant = assistant;
        _accounts = accounts;
        _logger = logger;
    }

    // customId: "kgsmact~<grant>" — the wildcard captures the grant.
    [ComponentInteraction(AssistantConfirmationIds.ConfirmPrefix + "*")]
    public async Task ConfirmAsync(string token)
    {
        // Re-resolved at the click rather than trusted from the staging turn: the account someone
        // held when the button was posted is not necessarily the one they hold now. A refusal leaves
        // the prompt standing, so whoever IS permitted can still use it.
        AccountAnswer account = await _accounts.ResolveAsync(Context.User.Id);
        if (!account.Allows(KgsmTier.Operator))
        {
            await RespondAsync(account.Refusal(KgsmTier.Operator), ephemeral: true);
            return;
        }

        var component = (SocketMessageComponent)Context.Interaction;

        // Ack inside Discord's ~3s window and clear the buttons so the same grant cannot be clicked
        // twice, THEN do the slow part. The assistant refuses a second redemption anyway; this is so
        // nobody is left looking at a live-looking button while the first click is still running.
        await component.UpdateAsync(m =>
        {
            m.Content = "⏳ Working on it…";
            m.Components = new ComponentBuilder().Build();
        });

        // No provenance scope: the action runs in the assistant's process, which records it from the
        // identity and the leaf name this call carries. Nothing here reaches kgsm from inside the bot.
        var result = await _assistant.ConfirmAsync(new AssistantApproval(
            Context.User.Id.ToString(), Context.User.Username, account.Tier, token));

        if (result.IsFailure)
        {
            await component.ModifyOriginalResponseAsync(m => m.Content = $"⚠️ {result.Error}");
            return;
        }

        var outcome = result.Value!;
        _logger.LogInformation(
            "Confirmed an assistant action for {User}: success={Success} verdict={Verdict}",
            Context.User.Username, outcome.Success, outcome.Verdict ?? "(none)");

        await component.ModifyOriginalResponseAsync(m => m.Content = Describe(outcome));
    }

    /// <summary>
    /// Dismisses the prompt. Nothing is told to the assistant: the grant simply goes unredeemed and
    /// expires, which is the same outcome as walking away from it.
    /// </summary>
    [ComponentInteraction(AssistantConfirmationIds.Cancel)]
    public async Task CancelAsync()
    {
        var component = (SocketMessageComponent)Context.Interaction;
        await component.UpdateAsync(m =>
        {
            m.Content = "❌ Cancelled — nothing was changed.";
            m.Components = new ComponentBuilder().Build();
        });
    }

    /// <summary>
    /// Reports what is actually known. The assistant separates "the engine accepted this" from "the
    /// server got there", so a command that ran without arriving says so rather than being shown as
    /// a success — a claim nobody can check is worse than an honest partial one.
    /// </summary>
    private static string Describe(AssistantOutcome outcome)
    {
        var text = string.IsNullOrWhiteSpace(outcome.Text) ? "Done." : outcome.Text;
        var mark = outcome.Success ? "✅" : "⚠️";
        var note = outcome.Verdict switch
        {
            "notSettled" => "\n⏳ It was accepted but hasn't got there yet — check its status in a moment.",
            "unknown" => "\n❔ I couldn't read its state afterwards, so I can't tell you where it ended up.",
            _ => string.Empty,
        };
        return $"{mark} {text}{note}";
    }
}
