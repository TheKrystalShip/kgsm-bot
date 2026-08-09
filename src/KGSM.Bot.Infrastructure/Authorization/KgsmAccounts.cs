using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace KGSM.Bot.Infrastructure.Authorization;

/// <summary>
/// What the account store says about the Discord account that is asking.
/// </summary>
/// <remarks>
/// Four answers rather than a tier, because a refusal has to say which of these it is. "You are not
/// allowed" is the wrong thing to tell someone whose account nobody has connected yet, and a worse
/// thing to tell them when the store simply could not be read.
/// </remarks>
public enum AccountOutcome
{
    /// <summary>An account this Discord identity proves, and it may be used. Its tier is on the answer.</summary>
    Ok,

    /// <summary>No KGSM account here has this Discord account connected to it.</summary>
    NotLinked,

    /// <summary>The account exists and has been switched off.</summary>
    Disabled,

    /// <summary>The store could not be read, so nothing is known either way.</summary>
    Unreadable,
}

/// <summary>
/// The account a Discord user proves on this host, and what it lets them do here.
/// </summary>
/// <param name="Outcome">Which of the four answers this is.</param>
/// <param name="Tier">The tier to authorize on. <see cref="KgsmTier.None"/> for anything but <see cref="AccountOutcome.Ok"/>.</param>
/// <param name="Account">The KGSM username, when there is one.</param>
/// <param name="Reason">Why the store could not be read, on <see cref="AccountOutcome.Unreadable"/>.</param>
public readonly record struct AccountAnswer(
    AccountOutcome Outcome,
    KgsmTier Tier,
    string? Account = null,
    string? Reason = null)
{
    /// <summary>Whether this answer clears a gate set at <paramref name="minimum"/>.</summary>
    public bool Allows(KgsmTier minimum) => Outcome == AccountOutcome.Ok && Tier >= minimum;

    /// <summary>
    /// What to tell the person, when it does not. A complete sentence, so every surface that refuses
    /// somebody refuses them in the same words and none of them has to guess at the reason.
    /// </summary>
    public string Refusal(KgsmTier minimum) => Outcome switch
    {
        AccountOutcome.NotLinked =>
            "🔗 Your Discord account isn't connected to a KGSM account on this host, so I don't know " +
            "who you are here. Ask an admin for an account, then connect this Discord account to it " +
            "in the Control Panel under Settings → Connected accounts.",

        AccountOutcome.Disabled =>
            $"🚫 Your KGSM account (`{Account}`) is disabled, so nothing here will act for you. " +
            "An admin can turn it back on.",

        AccountOutcome.Unreadable =>
            "⚠️ I couldn't read this host's KGSM accounts, so I can't tell what you're allowed to do. " +
            "Nothing was done. Ask an admin to check the bot's log.",

        // An account that authenticates and holds nothing: newly arrived and not approved yet, or
        // approved and deliberately left with no access. Naming the tier it lacks would be a riddle.
        _ when Tier == KgsmTier.None =>
            $"⏳ Your KGSM account (`{Account}`) doesn't have any access on this host yet — an admin " +
            "has to approve it in the Control Panel.",

        _ =>
            $"⛔ This needs **{KgsmTiers.ToWire(minimum)}**; your KGSM account (`{Account}`) holds " +
            $"**{KgsmTiers.ToWire(Tier)}**. An admin can change that in the Control Panel.",
    };
}

/// <summary>
/// Turning the Discord account the gateway hands the bot into the KGSM account it proves.
/// </summary>
public interface IKgsmAccounts
{
    /// <summary>Whether the store could be opened at all.</summary>
    bool Available { get; }

    /// <summary>Why not, when <see cref="Available"/> is <see langword="false"/>.</summary>
    string? UnavailableReason { get; }

    /// <summary>What this Discord user may do here, right now.</summary>
    Task<AccountAnswer> ResolveAsync(ulong discordUserId, CancellationToken ct = default);
}

/// <summary>
/// This host's KGSM accounts, read straight off the shared store file.
/// </summary>
/// <remarks>
/// <para>
/// The bot has no login of its own, so the Discord account Discord tells it about <em>is</em> the
/// identity — and the tier is whatever KGSM account that identity is connected to, exactly as it is
/// for a browser that signed in with a password. Guild membership and guild roles are facts about a
/// chat server and are not consulted: the gate is having an account here, which an admin granted, and
/// that is a narrower thing than being in the guild.
/// </para>
/// <para>
/// <b>Uncached.</b> The read behind it is a point query against a local file, and Discord commands
/// arrive at typing speed — so an admin changing somebody's tier in the Control Panel lands on their
/// very next command, with no window in which the bot still honours the old one.
/// </para>
/// <para>
/// <b>Opening the store can fail, and that must not stop the bot.</b> A permission problem or a store
/// written by a newer build leaves announcements, channel status and the journal reader working while
/// every gated command refuses with the reason. Refusing to start would let another service's deploy
/// order decide whether Discord has a bot at all.
/// </para>
/// </remarks>
public sealed class KgsmAccounts : IKgsmAccounts
{
    private readonly UserStoreAuthority? _authority;

    public KgsmAccounts(IOptions<AuthOptions> options, ILogger<KgsmAccounts> logger)
    {
        string path = options.Value.UsersDbPath;

        try
        {
            _authority = new UserStoreAuthority(new SqliteUserStore(new UserStoreOptions { Path = path }));
            logger.LogInformation("KGSM account store opened at {Path}.", path);
        }
        catch (UserStoreSchemaException e)
        {
            UnavailableReason = e.Message;
            logger.LogError(e,
                "KGSM account store at {Path} is a schema this build does not understand. Every " +
                "command that needs authorization will refuse until this bot is brought up to the " +
                "same version as the rest of the host.", path);
        }
        catch (Exception e)
        {
            UnavailableReason = $"The KGSM account store at '{path}' could not be opened.";
            logger.LogError(e,
                "KGSM account store at {Path} could not be opened. Every command that needs " +
                "authorization will refuse; announcements and channel status are unaffected.", path);
        }
    }

    /// <inheritdoc />
    public bool Available => _authority is not null;

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public async Task<AccountAnswer> ResolveAsync(ulong discordUserId, CancellationToken ct = default)
    {
        if (_authority is null)
            return new AccountAnswer(AccountOutcome.Unreadable, KgsmTier.None, Reason: UnavailableReason);

        // Only the subject identifies. The username on the identity is display, and the store keys a
        // credential by provider:subject precisely because a Discord handle is renameable.
        KgsmIdentity identity = new(
            KgsmActorProvider.Discord,
            discordUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Empty, string.Empty, null, []);

        AuthorityAnswer answer;
        try
        {
            answer = await _authority.ResolveAsync(identity, ct).ConfigureAwait(false);
        }
        catch (KgsmAuthProviderException e)
        {
            // A store that cannot be read is never reported as "you hold nothing" — that would
            // demote an admin mid-incident on the strength of a momentary I/O failure.
            return new AccountAnswer(AccountOutcome.Unreadable, KgsmTier.None, Reason: e.Message);
        }

        return answer.Outcome switch
        {
            AuthorityOutcome.Ok => new AccountAnswer(AccountOutcome.Ok, answer.Tier, answer.User?.Username),
            AuthorityOutcome.Disabled => new AccountAnswer(AccountOutcome.Disabled, KgsmTier.None, answer.User?.Username),
            _ => new AccountAnswer(AccountOutcome.NotLinked, KgsmTier.None),
        };
    }
}
