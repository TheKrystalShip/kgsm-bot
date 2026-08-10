using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Discord;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Handles the action buttons the bot posts beside an announcement.
/// </summary>
/// <remarks>
/// <para>
/// <b>An announcement is where somebody is already looking at the moment they care.</b> A crash tells
/// a person something is wrong and then makes them go and type a command; the button removes that
/// step, and removes nothing else — it runs the same path <c>/restart</c> runs, through the same
/// service, stamped with the same provenance.
/// </para>
/// <para>
/// <b>Authorized at the click, not at the post.</b> Nothing was authorized when the announcement went
/// out — an announcement has no caller — so the tier is resolved here, from the KGSM account the
/// clicker's Discord account is connected to. A refusal is ephemeral and leaves the button standing,
/// because whoever <i>is</i> permitted has not clicked it yet. This is the same rule
/// <see cref="AssistantConfirmationModule"/> follows, for the same reason.
/// </para>
/// </remarks>
public class ServerActionModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IServerService _server;
    private readonly IKgsmAccounts _accounts;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<ServerActionModule> _logger;

    public ServerActionModule(
        IServerService server,
        IKgsmAccounts accounts,
        IInvocationContext invocation,
        ILogger<ServerActionModule> logger)
    {
        _server = server;
        _accounts = accounts;
        _invocation = invocation;
        _logger = logger;
    }

    // customId: "kgsmsrv~restart~<instance>" — the wildcard captures the server's name.
    [ComponentInteraction(ServerActionIds.RestartPrefix + "*")]
    public async Task RestartAsync(string instance)
    {
        AccountAnswer account = await _accounts.ResolveAsync(Context.User.Id);
        if (!account.Allows(KgsmTier.Operator))
        {
            await RespondAsync(account.Refusal(KgsmTier.Operator), ephemeral: true);
            return;
        }

        var component = (SocketMessageComponent)Context.Interaction;

        // Acked inside Discord's three seconds and the button cleared before the slow part, so nobody
        // is left looking at a live-looking button while a restart is already running.
        await component.UpdateAsync(m =>
        {
            m.Components = new ComponentBuilder().Build();
        });

        // The provenance scope is what makes the engine's audit say who pressed it. Without it kgsm
        // falls back to the OS user, which would credit every button press to the service account.
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));

        _logger.LogInformation("Restarting {InstanceName} from an announcement button, pressed by {User}",
            instance, Context.User.Username);

        OperationResult result = await _server.RestartAsync(instance);

        // Reported as a reply in the channel rather than as an edit of the announcement: the
        // announcement is the record of what the server did, and what a person did about it is a
        // separate fact that belongs beside it, not written over it.
        await component.FollowupAsync(result.IsSuccess
            ? $"🔄 **{instance}** was restarted by {Context.User.Mention}."
            : $"⚠️ {Context.User.Mention} asked to restart **{instance}**, and it failed: {result.ErrorMessage}",
            allowedMentions: AllowedMentions.None);
    }
}
