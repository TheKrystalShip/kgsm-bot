# Authorization: the KGSM account behind a Discord user

**A Discord account says who you are; the roles of the KGSM account it is connected to say what you may
do.** The bot has no login of its own, so the Discord user the gateway names *is* the identity, resolved
by its `discord:<snowflake>` handle. What that account may do is evaluated by `AccessEvaluator` — the one
function every member evaluates with — over the cluster's authority replica, for an **action** at a
**target**: `kgsm:server.start` at `instance:walter/terraria#9f3c`, `bot:voice.use` at `node:walter`.
The Control Panel and the assistant evaluate the same person the same way, so all three agree by
construction rather than by each deriving an answer. Model and vocabulary:
`kgsm-docs/systems/authorization/`.

**A guild role grants nothing, and neither does guild membership.** The gate is a grant on the account,
strictly narrower than being in the Discord server, and the reason the slash commands are safe
registered globally.

**The replica is the node's, read and never written.** The node on this machine keeps it at
`/var/lib/kgsm/auth/users.db` (`Auth:UsersDbPath`) and applies what the auth anchor publishes; this bot
reads it through `AuthorityReplicaFile` and never opens it for writing — two writers of one file would
be the node's authority rewritten by an echo of itself. The assistant this bot forwards a question to
evaluates the same person against its own replica of the same source, and neither takes the other's
word for what somebody may do. The file is read directly rather than asked for over HTTP, so the bot
keeps authorizing people with every other leaf stopped, and a role changed in the panel reaches the
very next command.

**Targets are named the way grants name them** (`BotStanding`). This node is the member id the node
writes into the host file (`Auth:ProviderFilePath`, `HostProviderFile.Node`); a server is its install,
`instance:<node>/<name>#<nonce>`, with the nonce off the inventory, so a grant on one install never
reaches a reinstall under the same name. A server whose nonce cannot be read is judged at the node,
where an instance grant does not reach; a bot whose node has not named itself is judged at the cluster,
where only a cluster-wide grant does.

**Collections are cut to what the person can read** (`PersonAccess.FilterAsync`): `/list`, `/players`
and `/history` over the whole host, and the server autocomplete, show only the servers the asker holds
`kgsm:server.read` at. A total is summed over what they can see, never over the host with a part
hidden. A command over the whole host admits somebody holding the action at the node or at any server
on it (`AllowsSomewhere`).

`IBotAccess.ResolveAsync` gives **five** answers, and `PersonAccess.Refusal` writes the whole sentence
for each so every surface refuses somebody in the same words:

| standing | means | told |
|---|---|---|
| `Ok` | an active account; each action is asked of it | — or the action it lacks, by id and title |
| `NotLinked` | no KGSM account has this Discord account connected | how to connect it |
| `Pending` | the account is waiting to be approved and holds nothing | that it is waiting |
| `Disabled` | the account was switched off | that it is disabled |
| `Unreadable` | the replica could not be read | that nothing is known, and nothing was done |

`Unreadable` is deliberately not a denial. *"We could not ask"* is a different fact from *"the answer
is no"*, and reporting the first as the second demotes an Owner mid-incident. A refusal of an action
names it, and names a stale replica or an action nothing has declared for what they are.

## The bot's own service account

What the bot does with nobody asking — announcing, keeping the status message and presence current,
priming speech recognition with game names, investigating a give-up — it does as its own service
account, `svc:bot@<member>`. Its requirements are declared where the work is: `ServiceRequirements.cs`
for the engine reads it makes on its own, and `IncidentTriage` for asking the assistant and reading the
console of the run that died. Calls made for a person sit below that person's check and carry
`[PerformedFor]`, so the action manifest's generator can tell the two apart and fails the build on an
engine call neither covers.

**The bot reports its manifest twice, on purpose.** The node on this machine reports every manifest in
`/var/lib/kgsm/leaves/actions/`, which is how the bot's actions reach the catalog with no cluster at
all. The bot also reports the same file as a member of the cluster (`AuthorityReporter`), which is what
gives it a service account on its own member id — the only handle the assistant accepts from it for
crash triage, since a member may act only as its own service accounts.

How each surface applies it (slash preconditions, the @-mention surface, confirm buttons):
`src/KGSM.Bot.Discord/Commands/CLAUDE.md`.
