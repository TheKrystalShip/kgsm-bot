using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Requires the caller to hold at least <paramref name="minimum"/> on this host. The tier comes from
/// the shared role map (<c>TheKrystalShip.KGSM.Auth</c>), so a person gets the same authority here as
/// they do in the Control Panel and the assistant.
/// <para>
/// The bot resolves the tier from the member object the gateway already gives it — no REST call, no
/// token, no cache. That is the whole reason this leaf takes only the model half of the shared auth
/// packages.
/// </para>
/// </summary>
/// <remarks>
/// Fail-closed in both directions that matter. A caller who is not a guild member — a slash command
/// run in a DM, where <c>Context.User</c> is not a <see cref="SocketGuildUser"/> — has no member
/// object, which the role map reads as "not a member" and denies. A host that configured no role ids
/// leaves every member at viewer, so an operator command refuses everyone until the roles are set,
/// rather than admitting everyone.
/// </remarks>
internal class RequireTierAttribute(KgsmTier minimum) : PreconditionAttribute
{
    public KgsmTier Minimum { get; } = minimum;

    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo command, IServiceProvider services)
    {
        KgsmRoleMap roleMap = services.GetRequiredService<KgsmRoleMap>();

        // A non-member has no roles to read. null (not the empty list) is what the map reads as "not
        // a member" — passing an empty list here would floor them at viewer and let a DM read the host.
        KgsmTier tier = roleMap.ResolveSnowflakes(
            (context.User as SocketGuildUser)?.Roles.Select(r => r.Id));

        return Task.FromResult(tier >= Minimum
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(
                $"this needs {KgsmTiers.ToWire(Minimum)}; you hold {KgsmTiers.ToWire(tier)}."));
    }
}
