using Discord;
using Discord.Interactions;

using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth.Access;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Requires the caller to hold <paramref name="action"/> where the command acts: at the server its
/// <see cref="Server"/> option names, or at this node. What they hold comes from the KGSM account their
/// Discord account is connected to, evaluated against the cluster's authority by the same function the
/// Control Panel and the assistant evaluate with, so a person can do the same things here as there.
/// <para>
/// The bot signs nobody in: the Discord account the gateway names is the identity, and the replica
/// answers the rest. A guild role is a fact about a chat server and is not consulted.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// A command over the whole host — its server option left out — needs the action at this node or at
/// some server on it, and then shows only the servers it is held at. A list is cut to what this person
/// can see, never the host with a part hidden.
/// </para>
/// <para>
/// Fail-closed in every direction, and explicit about which one. A Discord account nobody has connected
/// is refused with how to connect it; a pending or disabled account is told so; and a replica that
/// could not be read refuses without claiming anything about the caller, since "we could not ask" is not
/// "the answer is no".
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequireActionAttribute(string action) : PreconditionAttribute
{
    /// <summary>The action, in full: <c>kgsm:server.start</c>.</summary>
    public string Action { get; } = action;

    /// <summary>
    /// The option that names the server the command acts on, or <see langword="null"/> for a command
    /// that acts on this node. Left out by whoever types it, the command is over the whole host.
    /// </summary>
    public string? Server { get; init; }

    /// <summary>A command over every server on the host, which shows each person the ones they can see.</summary>
    public bool AnyServer { get; init; }

    public override async Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo command, IServiceProvider services)
    {
        PersonAccess person = await services.GetRequiredService<IBotAccess>().ResolveAsync(context.User.Id);

        // The whole refusal, not a fragment: the handler prints this verbatim, because prefixing a
        // "you don't have permission" onto "your account isn't connected" tells somebody the one thing
        // that is not true about their situation.
        if (person.Outcome != AccountStanding.Ok)
            return PreconditionResult.FromError(person.Refusal(Action));

        string? server = Server is null ? null : OptionValue(context, Server);
        if (AnyServer || (Server is not null && server is null))
        {
            return person.AllowsSomewhere(Action)
                ? PreconditionResult.FromSuccess()
                : PreconditionResult.FromError(person.Refusal(Action));
        }

        AccessDecision decision = await person.DecideAsync(Action, server);
        return decision.Allowed
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(person.Refusal(Action, decision));
    }

    /// <summary>
    /// The value given for the option <paramref name="name"/>, wherever it sits under a group's
    /// subcommands, or <see langword="null"/> when it was left out.
    /// </summary>
    private static string? OptionValue(IInteractionContext context, string name) =>
        context.Interaction is ISlashCommandInteraction slash ? Find(slash.Data.Options, name) : null;

    private static string? Find(IEnumerable<IApplicationCommandInteractionDataOption>? options, string name)
    {
        foreach (IApplicationCommandInteractionDataOption option in options ?? [])
        {
            if (option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup)
            {
                if (Find(option.Options, name) is { } nested)
                    return nested;

                continue;
            }

            if (string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)
                && option.Value?.ToString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }
}
