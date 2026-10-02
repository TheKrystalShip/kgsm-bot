using System.Reflection;

using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// A test's stand-in for the auth anchor and the node's replica: an authority store of its own, the
/// accounts and grants a test makes in it, and the snapshot the anchor would send delivered into a
/// replica file after every change — so the bot reads exactly what replication would have left on the
/// node, through the same reader it runs with.
/// </summary>
/// <remarks>
/// Real files rather than a substitute, because the thing worth pinning is that the bot evaluates the
/// rows the anchor writes: a fake would agree with whatever this code believes the authority is.
/// </remarks>
internal sealed class TestAuthority : IDisposable
{
    /// <summary>The node the bot under test sits on.</summary>
    public const string Node = "walter";

    // Long enough that no test outlives it: the replica stays current for the whole run.
    private static readonly TimeSpan Bound = TimeSpan.FromDays(1);

    private readonly string _dir = Directory.CreateTempSubdirectory("kgsm-bot-authority").FullName;
    private readonly SqliteAuthorityStore _anchor;
    private readonly SqliteAuthorityStore _replica;
    private readonly string _root;

    public TestAuthority()
    {
        ReplicaPath = Path.Combine(_dir, "users.db");
        _anchor = new SqliteAuthorityStore(new UserStoreOptions { Path = Path.Combine(_dir, "anchor.db") });
        _replica = new SqliteAuthorityStore(new UserStoreOptions { Path = ReplicaPath });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Run(async () =>
        {
            await _anchor.ReplaceCatalogAsync(Catalog(), now);
            var root = new KgsmIdentity("local", "root", "root", "root", null, []);
            KgsmUser account = (await new IdentityLinkService(_anchor)
                .ProvisionAsync(root, AccountOrigin.Admitted, UserStatus.Active, now)).User!;
            await _anchor.GrantOwnerLocallyAsync(account.Username, "local:test", now);
            await DeliverAsync(now);
            return account.UserId;
        }, out _root);
    }

    /// <summary>The node's replica file, as the bot is configured to read it.</summary>
    public string ReplicaPath { get; }

    /// <summary>
    /// An account <paramref name="discordUserId"/>'s Discord account is connected to, named
    /// <paramref name="username"/>, at <paramref name="status"/>, holding no role. Returns its id.
    /// </summary>
    public string Person(ulong discordUserId, string username = "haru", UserStatus status = UserStatus.Active)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Run(async () =>
        {
            var account = new KgsmUser(
                UserIds.NewUserId(), username, username, AccountOrigin.Admitted, status, now, now);
            await _anchor.CreateAsync(account);
            await _anchor.AddCredentialAsync(new UserCredential(
                "cred_" + username, account.UserId, CredentialKind.Identity,
                DiscordHandle.Of(discordUserId), null, null, now, null));
            await DeliverAsync(now);
            return account.UserId;
        }, out string id);
        return id;
    }

    /// <summary>Grant <paramref name="accountId"/> one action at <paramref name="scope"/>, through a role of its own.</summary>
    public void Grant(string accountId, string action, AccessScope scope)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Run(async () =>
        {
            string name = "grant-" + Guid.NewGuid().ToString("N")[..8];
            string permission = (await EditAsync(new CreatePermission(name), now)).CreatedId!;
            await EditAsync(new SetPermissionActions(permission, new HashSet<string>(StringComparer.Ordinal) { action }), now);
            string role = (await EditAsync(new CreateRole(name), now)).CreatedId!;
            await EditAsync(new SetRolePermissions(role, new HashSet<string> { permission }), now);
            await EditAsync(new Assign(accountId, role, scope), now);
            await DeliverAsync(now);
            return true;
        }, out _);
    }

    /// <summary>Make the account named <paramref name="username"/> an Owner.</summary>
    public void Owner(string username)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Run(async () =>
        {
            await _anchor.GrantOwnerLocallyAsync(username, "local:test", now);
            await DeliverAsync(now);
            return true;
        }, out _);
    }

    /// <summary>
    /// The bot's access over this replica, on <paramref name="node"/>, with <paramref name="servers"/>
    /// as the servers on it and the install nonce each one carries.
    /// </summary>
    public IBotAccess Access(IReadOnlyDictionary<string, string>? servers = null, string? node = Node)
    {
        IKgsmStateCache cache = Substitute.For<IKgsmStateCache>();
        foreach ((string name, string nonce) in servers ?? new Dictionary<string, string>())
        {
            cache.GetInstanceAsync(name, Arg.Any<CancellationToken>())
                .Returns(new Instance { Name = name, InstallNonce = nonce });
        }

        return new BotAccess(
            new MemberAccess(new AuthorityReplicaFile(ReplicaPath, NullLogger<AuthorityReplicaFile>.Instance)),
            new BotStanding(() => node, cache));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<AuthorityWrite> EditAsync(AuthorityEdit edit, DateTimeOffset now) =>
        await _anchor.ApplyAsync(_root, edit, await _anchor.VersionAsync(), now);

    private async Task DeliverAsync(DateTimeOffset now) =>
        await _replica.ApplySnapshotAsync(await _anchor.ExportAsync(Bound, now), now);

    private static void Run<T>(Func<Task<T>> work, out T result) => result = work().GetAwaiter().GetResult();

    /// <summary>
    /// Every action the bot checks, at the scope the component performing it declares: a server's at
    /// the instance, the engine's node-wide ones at the node, the library across the cluster, the bot's
    /// own at the node and talking to the assistant across the cluster, which every person holds.
    /// </summary>
    private static IReadOnlyCollection<CatalogAction> Catalog() =>
    [
        .. new[] { typeof(KgsmActions), typeof(BotActions) }
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(id => id.Contains(':') && !id.Contains('/'))
            .Distinct(StringComparer.Ordinal)
            .Select(id => new CatalogAction(id, id, EffectOf(id), ScopeOf(id), Self: id == BotActions.AssistantChat)),
    ];

    private static ActionEffect EffectOf(string id) =>
        id.EndsWith(".read", StringComparison.Ordinal) ? ActionEffect.Read : ActionEffect.Write;

    private static ScopeKind ScopeOf(string id) => id switch
    {
        KgsmActions.LibraryRead => ScopeKind.Cluster,
        KgsmActions.ServerInstall => ScopeKind.Node,
        _ when id.StartsWith("assistant:", StringComparison.Ordinal) => ScopeKind.Cluster,
        _ when id.StartsWith("kgsm:server.", StringComparison.Ordinal) => ScopeKind.Instance,
        _ => ScopeKind.Node,
    };
}
