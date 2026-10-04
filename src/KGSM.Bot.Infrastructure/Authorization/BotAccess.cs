using System.Globalization;

using KGSM.Bot.Core.Interfaces;

using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.Authorization;

/// <summary>
/// What the cluster's authority says about the Discord account that is asking.
/// </summary>
/// <remarks>
/// Five answers rather than a yes or no, because a refusal has to say which of these it is. "You are
/// not allowed" is the wrong thing to tell someone whose account nobody has connected yet, and a worse
/// thing to tell them when the replica simply could not be read.
/// </remarks>
public enum AccountStanding
{
    /// <summary>An active account this Discord identity proves. What it may do is asked per action.</summary>
    Ok,

    /// <summary>No KGSM account has this Discord account connected to it.</summary>
    NotLinked,

    /// <summary>The account exists and is waiting to be approved, so it holds nothing.</summary>
    Pending,

    /// <summary>The account exists and has been switched off.</summary>
    Disabled,

    /// <summary>The replica could not be read, so nothing is known either way.</summary>
    Unreadable,
}

/// <summary>
/// A Discord user's account on this node, and the questions that can be put about what it may do here.
/// </summary>
/// <remarks>
/// Every question is answered over the replica as it stood when the account was resolved, so one
/// command is judged against one state of the authority rather than whatever a write between two of its
/// checks left behind.
/// </remarks>
public sealed class PersonAccess
{
    private readonly AccessEvaluator? _evaluator;
    private readonly string? _accountId;
    private readonly BotStanding _standing;

    private PersonAccess(
        AccountStanding outcome, string? account, string? reason,
        AccessEvaluator? evaluator, string? accountId, BotStanding standing)
    {
        Outcome = outcome;
        Account = account;
        Reason = reason;
        _evaluator = evaluator;
        _accountId = accountId;
        _standing = standing;
    }

    /// <summary>Which of the five answers this is.</summary>
    public AccountStanding Outcome { get; }

    /// <summary>The KGSM username, when there is an account.</summary>
    public string? Account { get; }

    /// <summary>Why the replica could not be read, on <see cref="AccountStanding.Unreadable"/>.</summary>
    public string? Reason { get; }

    internal static PersonAccess Without(AccountStanding outcome, BotStanding standing, string? account = null, string? reason = null) =>
        new(outcome, account, reason, null, null, standing);

    internal static PersonAccess For(AccessEvaluator evaluator, string accountId, string account, BotStanding standing) =>
        new(AccountStanding.Ok, account, null, evaluator, accountId, standing);

    /// <summary>
    /// Whether they may perform <paramref name="action"/> at <paramref name="instance"/>, or at this node
    /// when no server is named.
    /// </summary>
    public async Task<AccessDecision> DecideAsync(string action, string? instance, CancellationToken ct = default)
    {
        if (_evaluator is null || _accountId is null)
            return AccessDecision.Deny(DenyReason.NoAccount);

        AccessScope target = instance is { Length: > 0 }
            ? await _standing.InstallAsync(instance, ct).ConfigureAwait(false)
            : _standing.Here();

        return _evaluator.Allows(_accountId, action, target);
    }

    /// <inheritdoc cref="DecideAsync"/>
    public async Task<bool> AllowsAsync(string action, string? instance, CancellationToken ct = default) =>
        (await DecideAsync(action, instance, ct).ConfigureAwait(false)).Allowed;

    /// <summary>
    /// Whether they may perform <paramref name="action"/> at this node or at any server on it — what a
    /// command over the whole host needs before it shows them the part of it they can see.
    /// </summary>
    public bool AllowsSomewhere(string action)
    {
        if (_evaluator is null || _accountId is null)
            return false;

        AccessScope here = _standing.Here();
        if (_evaluator.Allows(_accountId, action, here).Allowed)
            return true;

        foreach (Assignment assignment in _evaluator.Snapshot.AssignmentsOf(_accountId))
        {
            if (here.Contains(assignment.Scope) && _evaluator.Allows(_accountId, action, assignment.Scope).Allowed)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The servers among <paramref name="instances"/> they may perform <paramref name="action"/> at, in
    /// the order given — a list cut to what this person can see, never the whole host with a part hidden.
    /// </summary>
    public async Task<IReadOnlyList<string>> FilterAsync(
        string action, IEnumerable<string> instances, CancellationToken ct = default)
    {
        List<string> allowed = [];
        foreach (string instance in instances)
        {
            if (await AllowsAsync(action, instance, ct).ConfigureAwait(false))
                allowed.Add(instance);
        }

        return allowed;
    }

    /// <summary>
    /// What to tell the person when they may not. A complete sentence, so every surface that refuses
    /// somebody refuses them in the same words and none of them has to guess at the reason.
    /// </summary>
    public string Refusal(string action, AccessDecision decision = default) => Outcome switch
    {
        AccountStanding.NotLinked =>
            "🔗 Your Discord account isn't connected to a KGSM account, so I don't know who you are " +
            "here. Ask for an account, then connect this Discord account to it in the Control Panel " +
            "under Settings → Connected accounts.",

        AccountStanding.Pending =>
            $"⏳ Your KGSM account (`{Account}`) is waiting to be approved, so it can't do anything " +
            "here yet. It's approved in the Control Panel.",

        AccountStanding.Disabled =>
            $"🚫 Your KGSM account (`{Account}`) is disabled, so nothing here will act for you. " +
            "Ask whoever manages accounts to turn it back on.",

        AccountStanding.Unreadable =>
            "⚠️ I couldn't read this host's copy of the cluster's accounts, so I can't tell what " +
            "you're allowed to do. Nothing was done. Ask whoever runs this host to check the bot's log.",

        _ => decision.Reason switch
        {
            DenyReason.Stale =>
                "⚠️ This host's copy of the cluster's accounts is out of date, so it serves only reads " +
                $"until it catches up. `{action}` changes something, so nothing was done.",

            DenyReason.UnknownAction =>
                $"⛔ This needs `{action}`, which nothing in this cluster has declared yet, so only an " +
                "Owner can do it.",

            DenyReason.ContractOutdated =>
                "⚠️ This bot is older than the cluster's accounts allow, so it refuses everything until " +
                "it is upgraded. Nothing was done.",

            _ =>
                $"⛔ This needs `{action}`{Titled(action)}, and your KGSM account (`{Account}`) doesn't " +
                "hold it here. A role holding it is granted in the Control Panel.",
        },
    };

    /// <summary>The action's title from the catalog, as the role editor shows it, when it has one.</summary>
    private string Titled(string action) =>
        _evaluator?.Snapshot.Catalog.TryGetValue(action, out CatalogAction? entry) == true && entry.Title.Length > 0
            ? $" ({entry.Title})"
            : string.Empty;
}

/// <summary>
/// Where this bot is, as a target for what it is asked to do: the node it sits on, and each server on it
/// as the install it is.
/// </summary>
/// <param name="node">The node this bot sits on, as that node wrote it, or <see langword="null"/> while it has not.</param>
/// <param name="instances">This node's servers, for the install nonce each one carries.</param>
internal sealed class BotStanding(Func<string?> node, IKgsmStateCache instances)
{
    /// <summary>
    /// This node, and the cluster while the node has not said which it is — where only a grant across
    /// the whole cluster can be told to reach it.
    /// </summary>
    public AccessScope Here() => node() is { Length: > 0 } id ? AccessScope.ForNode(id) : AccessScope.Cluster;

    /// <summary>
    /// The install a server is, as narrowly as it can be named: the node and nonce, the node alone
    /// while the nonce is unknown — so a grant on one install never reaches a server it cannot name.
    /// </summary>
    public async Task<AccessScope> InstallAsync(string instance, CancellationToken ct)
    {
        if (node() is not { Length: > 0 } id)
            return AccessScope.Cluster;

        Instance? found;
        try
        {
            found = await instances.GetInstanceAsync(instance, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            found = null;
        }

        return found?.InstallNonce is { Length: > 0 } nonce
            ? AccessScope.ForInstance(id, instance, nonce)
            : AccessScope.ForNode(id);
    }
}

/// <summary>How a Discord account is named to anything that resolves it against the cluster's accounts.</summary>
public static class DiscordHandle
{
    /// <summary>
    /// <c>discord:&lt;snowflake&gt;</c>. Only the subject identifies: a Discord username is renameable,
    /// so a credential is keyed by provider and subject, and a bare id would name nobody.
    /// </summary>
    public static string Of(ulong discordUserId) =>
        KgsmActor.Format(KgsmActorProvider.Discord, discordUserId.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// Turning the Discord account the gateway hands the bot into the KGSM account it proves, and what that
/// account may do here.
/// </summary>
public interface IBotAccess
{
    /// <summary>Whether the replica can be read.</summary>
    bool Available { get; }

    /// <summary>Why not, when <see cref="Available"/> is <see langword="false"/>.</summary>
    string? UnavailableReason { get; }

    /// <summary>Who this Discord user is here, right now, and what they may do.</summary>
    Task<PersonAccess> ResolveAsync(ulong discordUserId, CancellationToken ct = default);
}

/// <summary>
/// A Discord user's access on this node, evaluated from the cluster's authority replica.
/// </summary>
/// <remarks>
/// <para>
/// The bot has no sign-in of its own, so the Discord account the gateway names <em>is</em> the identity,
/// and what it may do is whatever the KGSM account that identity is connected to holds — evaluated by the
/// same function every member evaluates with, over the replica the node on this machine keeps. Guild
/// membership and guild roles are facts about a chat server and are not consulted.
/// </para>
/// <para>
/// <b>Read on every command.</b> The replica is a local file and Discord commands arrive at typing speed,
/// so a role changed in the Control Panel reaches somebody's very next command.
/// </para>
/// <para>
/// <b>A replica that cannot be read must not stop the bot.</b> Announcements, channel status and the
/// journal reader keep working while every checked command refuses with the reason.
/// </para>
/// </remarks>
internal sealed class BotAccess(MemberAccess access, BotStanding standing) : IBotAccess
{
    /// <inheritdoc />
    public bool Available => access.UnavailableReason is null;

    /// <inheritdoc />
    public string? UnavailableReason => access.UnavailableReason;

    /// <inheritdoc />
    public async Task<PersonAccess> ResolveAsync(ulong discordUserId, CancellationToken ct = default)
    {
        // Only the subject identifies. A Discord handle is renameable, so a credential is keyed by
        // provider:subject and the name on the identity is display.
        KgsmIdentity identity = new(
            KgsmActorProvider.Discord,
            discordUserId.ToString(CultureInfo.InvariantCulture),
            string.Empty, string.Empty, null, []);

        MemberAccessCaller caller = await access.ResolveAsync(identity, ct).ConfigureAwait(false);

        switch (caller.Refusal)
        {
            case MemberAccessRefusal.Unavailable:
                // "We could not ask" is never reported as "you hold nothing": that would demote an
                // Owner mid-incident on the strength of an outage.
                return PersonAccess.Without(AccountStanding.Unreadable, standing, reason: caller.Reason);

            case MemberAccessRefusal.NoAccount:
                return PersonAccess.Without(AccountStanding.NotLinked, standing);

            case MemberAccessRefusal.AccountDisabled:
                return PersonAccess.Without(AccountStanding.Disabled, standing,
                    await NameOfAsync(caller.AccountId, ct).ConfigureAwait(false));
        }

        AccessEvaluator evaluator = caller.Evaluator!;
        AccessAccount held = evaluator.Snapshot.Accounts[caller.AccountId!];
        return held.Status == AccountStatus.Pending
            ? PersonAccess.Without(AccountStanding.Pending, standing, held.Name)
            : PersonAccess.For(evaluator, held.AccountId, held.Name, standing);
    }

    /// <summary>An account's username, for a refusal that has no evaluator to read it from.</summary>
    private async Task<string?> NameOfAsync(string? accountId, CancellationToken ct) =>
        accountId is not null
        && await access.EvaluatorAsync(ct).ConfigureAwait(false) is { } evaluator
        && evaluator.Snapshot.Accounts.TryGetValue(accountId, out AccessAccount? account)
            ? account.Name
            : null;
}
