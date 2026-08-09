using Discord;
using Discord.Interactions;

using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Requires the caller to hold at least <paramref name="minimum"/> on this host. The tier comes from
/// the KGSM account their Discord account is connected to, so a person gets the same authority here
/// as they do in the Control Panel and the assistant — all three read the same record rather than
/// each deriving one.
/// <para>
/// The bot signs nobody in: the Discord account the gateway names is the identity, and the account
/// store answers the rest. A guild role is a fact about a chat server and is not consulted.
/// </para>
/// </summary>
/// <remarks>
/// Fail-closed in every direction, and explicit about which one. A Discord account nobody has
/// connected here is refused with how to connect it; a disabled account is told it is disabled; and a
/// store that could not be read refuses without claiming anything about the caller's authority, since
/// "we could not ask" is not "the answer is no".
/// </remarks>
internal class RequireTierAttribute(KgsmTier minimum) : PreconditionAttribute
{
    public KgsmTier Minimum { get; } = minimum;

    public override async Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo command, IServiceProvider services)
    {
        IKgsmAccounts accounts = services.GetRequiredService<IKgsmAccounts>();
        AccountAnswer answer = await accounts.ResolveAsync(context.User.Id);

        // The whole refusal, not a fragment: the handler prints this verbatim, because prefixing a
        // "you don't have permission" onto "your account isn't connected" tells somebody the one
        // thing that is not true about their situation.
        return answer.Allows(Minimum)
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(answer.Refusal(Minimum));
    }
}
