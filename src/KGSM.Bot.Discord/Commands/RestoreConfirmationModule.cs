using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// The second half of a restore: the button that runs the one <c>/restore</c> proposed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorized at the click, not at the proposal</b> — the same rule the restart button and the
/// assistant's confirmations follow. The tier somebody held when the message was posted is not
/// necessarily the one they hold now.
/// </para>
/// <para>
/// <b>And it must be the same person.</b> A restart button is a shortcut to a command anybody with
/// the tier could type, so anyone with the tier may press it. This one names a specific archive that
/// somebody else chose, sitting in a channel — pressing it is agreeing to a decision you did not
/// make, on a server you may not have been looking at.
/// </para>
/// <para>
/// <b>Redeeming is one-shot.</b> A confirmation that can be clicked twice is a restore that runs
/// twice, and the second one rolls back whatever the first produced.
/// </para>
/// </remarks>
public class RestoreConfirmationModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IStagedRestores _staged;
    private readonly IServerInstanceService _instances;
    private readonly IBackupInsight _backups;
    private readonly IKgsmAccounts _accounts;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<RestoreConfirmationModule> _logger;

    public RestoreConfirmationModule(
        IStagedRestores staged,
        IServerInstanceService instances,
        IBackupInsight backups,
        IKgsmAccounts accounts,
        IInvocationContext invocation,
        ILogger<RestoreConfirmationModule> logger)
    {
        _staged = staged;
        _instances = instances;
        _backups = backups;
        _accounts = accounts;
        _invocation = invocation;
        _logger = logger;
    }

    // customId: "kgsmrst~<handle>"
    [ComponentInteraction(RestoreActionIds.ConfirmPrefix + "*")]
    public async Task ConfirmAsync(string handle)
    {
        var component = (SocketMessageComponent)Context.Interaction;

        AccountAnswer account = await _accounts.ResolveAsync(Context.User.Id);
        if (!account.Allows(KgsmTier.Operator))
        {
            await RespondAsync(account.Refusal(KgsmTier.Operator), ephemeral: true);
            return;
        }

        // Looked at before it is taken: a click that turns out not to be allowed must not consume the
        // proposal it was not entitled to answer, or pressing somebody else's button would cancel it
        // for the person who is actually deciding.
        if (_staged.Peek(handle) is not StagedRestore restore)
        {
            await component.RespondAsync(
                "That restore is no longer waiting — it expired, was already run, or was cancelled. " +
                "Run `/restore` again if you still want it.", ephemeral: true);
            return;
        }

        if (restore.ProposedToDiscordUserId != Context.User.Id)
        {
            _logger.LogInformation(
                "{User} pressed a restore confirmation for {InstanceName} that somebody else proposed.",
                Context.User.Username, restore.InstanceName);

            await component.RespondAsync(
                "This isn't yours to confirm — somebody else asked for this restore, and it is still " +
                $"waiting for them. Run `/restore {restore.InstanceName}` if you want to do it yourself.",
                ephemeral: true);
            return;
        }

        // Taken now, and taken once: a confirmation that can be clicked twice is a restore that runs
        // twice. A lost race here is the second click finding nothing, which is the correct answer.
        if (_staged.Redeem(handle) is null)
        {
            await component.RespondAsync("That restore has already been run.", ephemeral: true);
            return;
        }

        // Acked inside the three seconds and the buttons cleared before the slow part, so nobody is
        // left looking at a live-looking Restore while one is already running.
        await component.UpdateAsync(m => m.Components = new ComponentBuilder().Build());

        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));

        _logger.LogInformation("Restoring {InstanceName} from {BackupId}, confirmed by {User}",
            restore.InstanceName, restore.BackupId, Context.User.Username);

        Result result = await _instances.RestoreBackupAsync(restore.InstanceName, restore.BackupId);

        // Whatever happened, what this host holds for that server has moved on from what was cached.
        _backups.Invalidate(restore.InstanceName);

        await component.FollowupAsync(result.IsSuccess
            ? $"♻️ **{restore.InstanceName}** was rolled back to `{restore.BackupId}` by {Context.User.Mention}."
            : $"⚠️ {Context.User.Mention} asked to roll **{restore.InstanceName}** back and it failed: {result.Error}",
            allowedMentions: AllowedMentions.None);
    }

    /// <summary>
    /// Cancelling is deliberately open to anyone who can see the message, where confirming is not.
    /// The asymmetry is the point: one of these destroys a server's current state and the other
    /// leaves everything exactly as it was, and whoever proposed it can simply run the command again.
    /// </summary>
    // customId: "kgsmrsx~<handle>"
    [ComponentInteraction(RestoreActionIds.CancelPrefix + "*")]
    public async Task CancelAsync(string handle)
    {
        var component = (SocketMessageComponent)Context.Interaction;

        _staged.Cancel(handle);

        await component.UpdateAsync(m =>
        {
            m.Content = "Cancelled — nothing was changed.";
            m.Embed = null;
            m.Components = new ComponentBuilder().Build();
        });
    }
}
