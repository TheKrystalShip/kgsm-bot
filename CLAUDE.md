# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-bot` is the **Discord surface onto KGSM** — one of the `kgsm-*` leaves in the
`tks` umbrella workspace. It is a **leaf consumer**: it reaches the engine only
through `kgsm-lib`, never by shelling `kgsm.sh` itself. Its conversational half is a
**client of the kgsm-assistant leaf** — one engine behind every surface, so a chat held
here is the same conversation the Control Panel and the assistant's own site show. Read the workspace root `../CLAUDE.md` + `../system-architecture.md` for the
ecosystem rules this repo inherits (the dependency spine, "never fabricate a
status", leaves-are-independently-deployable).

## Building

This repo builds standalone — a clone with no sibling checkout restores and builds. Every
dependency is a package.

**kgsm-lib comes from the org's GitHub Packages feed** (`nuget.config`) as
`TheKrystalShip.KGSM.Lib`, pinned by version in Core, Application and Infrastructure. Editing
`kgsm-lib/` changes nothing here until it is published at a new version and the three pins move
together — a published version is immutable, so the old trap of a same-version repack serving a
stale package is gone. The same applies to
`TheKrystalShip.Kgsm.Assistant.Relay` (the assistant's relay contract) and
`TheKrystalShip.KGSM.Auth`.

Targets **.NET 10** (the README still says 9 — trust the `.csproj`/the code).

## Commands

```bash
dotnet build kgsm-bot.sln
dotnet test  kgsm-bot.sln
dotnet test  kgsm-bot.sln --filter "FullyQualifiedName~ConfirmationIds"   # one class/test

cd src/KGSM.Bot.Discord && dotnet run        # run locally (reads kgsm-bot.settings.json — see below)

./deploy/setup.sh                             # ONCE per host — asks for sudo; provisions the headless deploy grant
./deploy/deploy.sh                            # build + deploy to systemd on this host (no sudo, no prompts)
```

`deploy/deploy.sh` publishes a **framework-dependent single-file** binary (the host
must have the .NET 10 runtime; it is not bundled), syncs it into `/opt/kgsm-bot`, and
manages the `kgsm-bot.service` unit. It builds **as the invoking user** and needs **no
privilege at all**: `/opt/kgsm-bot` is yours so the sync is a plain file write, the real
unit lives in **user-owned** `/etc/kgsm-bot/systemd/` with `/etc/systemd/system/kgsm-bot.service`
symlinked to it so a unit change is also a plain file write, and the `systemctl` verbs go
through a polkit rule scoped to this project's units. It refuses **before building**, with
*"run `deploy/setup.sh`"*, on an unprovisioned host.

`deploy/setup.sh` is the one that asks for sudo, once per host: it provisions all of the
above (prefix ownership, env file, unit symlink, polkit grant, enable) and then verifies the
grant using the same unprivileged calls `deploy.sh` makes. It is idempotent. Neither script
overwrites the live env file, so the Discord token survives provisioning and every redeploy.
Tests/local-run ignore the publish-only props.

The three files in `deploy/` (`deploy-common.sh` + the two entry points) are self-contained —
a standalone clone deploys with no other repo checked out. Every `kgsm-*` repo carries this
same pattern. If some *other* operation seems to need root, stop and ask; don't reintroduce
`sudo` into `deploy.sh`.

## Configuration

`src/KGSM.Bot.Discord/kgsm-bot.settings.json` declares the bot's **whole** configurable
surface with its defaults, and is committed — it holds no secret and names no Discord server.
Key sections: `KgsmAuth` (the host's shared sign-in application), `Auth` (the account store — see
*Authorization*), `Guilds` (`DbPath` — the guild store, see *Where it announces*), `Discord`
(token, status markers, `PublicAddress`, `RemoveChannelOnInstanceDeletion`, `ActionButtons`,
`IncidentThreads`, the status-message cadence pair, the message-cleanup pair, the five `SendQueue`
keys, and the `Announce` switches), `KGSM` (`Path` to `kgsm.sh`, `JournalDir`, `WatchdogSocketPath`, `StatusSocketPath`,
`FirewallSocketPath`, and the `Blueprints` map), `Assistant` (where the assistant leaf is + the
shared relay secret), `KgsmCache` (inventory TTLs).

An environment variable **overrides one key** of that file by spelling the key's path with
`__` (`Discord__Token`), and a variable naming a key the file does not declare binds to
nothing. That is where the token lives (`/etc/kgsm-bot/kgsm-bot.env`). The descriptor generator
fails the build when the settings file and the annotated options classes disagree in either
direction, so a key added to one without the other never ships.

**`deploy/kgsm-bot.leaf.json` is generated, not written.** `TheKrystalShip.KGSM.LeafConfig`
rewrites it on every build from `[LeafField]` attributes and `<panel>` doc tags — so edit the
options classes, never the JSON, and commit what the build produces. `Discord`, `KGSM` and
`KgsmCache` carry theirs on their own types in `KGSM.Bot.Infrastructure`, which the generator
picks up because it scans every assembly beside the built binary. `KgsmAuth` is declared as
`[LeafFrameworkField]`s in `LeafDescriptor.cs` instead: that type belongs to
`TheKrystalShip.KGSM.Auth`, and each surface describes the same keys in its own words, so the prose
has to live with the surface that shows it. Format:
`../leaf-config-descriptor.md`; mechanism: `../kgsm-leafconfig/README.md`.

## Where it announces: `/setup` owns the topology

**The bot is guild-agnostic.** Nothing in configuration names a Discord server, and inviting the bot
somewhere grants that guild nothing: a guild hears about this host because an admin ran `/setup`
there, and a guild with no row in the store gets nothing whatever the bot's membership. The slash
commands are registered globally and authorize from the account store, so this is safe.

- **The store** is `Guilds:DbPath` — SQLite at `/var/lib/kgsm-bot/bot.db`, `0600`, the bot's own
  file and its only writer. **It is deliberately not under `/opt/kgsm-bot`**: `deploy.sh` syncs the
  prefix with `rsync -a --delete`, which would take it every deploy. The directory is the unit's
  `StateDirectory=kgsm-bot`, which systemd creates owned by `User=` before `ExecStart` — so it costs
  no privilege, and the store resolves from `$STATE_DIRECTORY` with the path above as the fallback
  for a bot run outside systemd. Nothing ever overwrites the file. **Additive-only**, with a
  `schema_version` row and a startup floor check that refuses a file a newer build wrote — losing this file loses every
  channel binding, and a binding is the only thing tying a server to the channel holding its
  history. Snowflakes are `TEXT`: a 64-bit unsigned id in a signed `INTEGER` column is a parse
  waiting to be got wrong.
- **One announcement mechanism, with the board as a layer on it.** `AnnounceAsync` iterates the
  configured guilds that follow the server in question and posts once in each; the only variable is
  which channel — the server's own where the guild runs a board and bound one, else the guild's
  announcement channel. `Render` names the server (`🟢 **factorio** started — … (heisen)`), so a
  message reads correctly out of either.
- **A binding is a preference; the announcement channel is the requirement.** A server's own channel
  deleted in Discord leaves a binding pointing at nothing, and the bot **falls back** to the guild's
  announcement channel rather than losing the announcement — treating the binding as the only place a
  server may report is how every message about it disappears silently for as long as nobody notices.
  ⚠ The stale binding is not repaired on that path: deciding a channel is really gone takes asking
  Discord, and doing it per announcement would spend a request fixing bookkeeping nobody is waiting
  on. `ReconcileBindingsAsync` does it once, on `Ready`.
- **A binding is dropped only on an answer that says the channel is gone.** ⚠ The gateway cache
  answers "deleted" and "the bot lost `View Channel`" with the same silence, and unbinding the second
  orphans a live channel full of a server's history with nothing pointing at it. So a cache miss is
  confirmed with a REST fetch — Discord returns nothing for a deleted channel and refuses for one it
  will not show — and **anything other than a clean "no such channel" leaves the binding alone**. A
  guild the bot cannot see at all is skipped entirely, so an outage drops nothing.
- **Run state lives in a message, never in a channel name.** Nothing here renames a channel, and
  nothing may: Discord rate-limits channel edits hard enough that a name cannot be kept in step with
  a server's state, and a bot that tries is throttled off the API — losing the announcements too. A
  marker in a channel name is a human's label; the bot's report of run state is the message it posts.
- **The live status message is the ambient board, and it is one message per guild.**
  `/setup status <channel>` starts keeping it and pins it; `/setup status-off` stops, leaving the
  message standing. It carries every server, whether it is up, and how to reach it, and
  `StatusBoardService` keeps it current by **editing** — the generous bucket the channel-name version
  could not use. Three rules hold it: an event marks it **dirty** and a floor
  (`StatusMessageMinIntervalSeconds`) decides when to spend an edit, so a host reboot's fifteen
  events cost **one**; a periodic republish (`StatusMessageRefreshSeconds`) is a backstop for what no
  event describes, never the mechanism; and the message id is **stored**, because a restart that
  posts a second board and keeps the wrong one current is a fabricated status with a timestamp on it.
  A run state that could not be read is marked unread (`❔`), never stopped. The snapshot is read
  **once for the host and narrowed per guild**, so a board cannot list a server the guild sitting
  beside it has unfollowed — and a guild following none of what is installed is told that, rather
  than being told the host is empty.
- **`/setup announce <channel>` alone is a working configuration.** The board — a channel per
  server, under a category — is opt-in per guild because it needs `Manage Channels`, which is the
  permission people reasonably think twice about granting. Enabled by *having a category*
  (`board_category_id NOT NULL`), never by a boolean beside one, because a flag and a category can
  disagree. `/setup board-off` and `/setup forget` **never delete a channel**: deleting one with
  history because a setting changed is not a decision a bot gets to make, and the reply says so.
- **`[RequireTier(KgsmTier.Admin)]`, never a Discord permission.** ⚠ Gating `/setup` on *Manage
  Server* would let anyone who can add the bot to a guild of their own point this host's
  announcements — including player joins and leaves — into it, and authorizing correctly would not
  help: an announcement has no caller to authorize. **Two questions, two refusals**: the tier
  (*may you configure this host*) and the bot's own Discord permission, checked **before** anything
  is recorded (*can I actually do it here*) — recording a channel it cannot post in is how a guild
  gets configured and then silently receives nothing. Not `[Mutating]`: it changes no server.
- **Each guild follows the servers it chooses, and `empty means all`.** `guild_servers` holds a
  guild's allowlist; **no rows is no filter**, which is what every guild configured before the filter
  existed already has — so nothing goes silent by upgrading, and a guild that wants silence runs
  `/setup forget` instead. `/setup follow` narrows (the *first* one narrows to that server alone, and
  the reply says so), `/setup unfollow` widens, `/setup follow-all` clears it. **Unfollowing the last
  one is refused**: emptying the list means *everything*, which is the opposite of what somebody
  removing their last server is asking for, so both real choices are named instead.
- **The filter governs what the bot says unprompted, never what it answers when asked.**
  Announcements, the per-server channel an install creates, and the rows on the live status message
  are filtered; slash commands are not. ⚠ Authority in this ecosystem is the KGSM account, host-wide
  — filtering *reads* by which guild they were typed in would be a second, per-guild authority model,
  which is exactly what `KgsmRoleMap` was and is banned (`auth-internal-users`). A viewer is trusted
  with this host's inventory wherever they ask from.
- **An unreadable filter follows everything.** Reading no rows and failing to read are both "no
  filter": the failure is loud in the log, and a guild an admin set up keeps hearing what it expected
  rather than going quiet for a reason invisible from inside Discord.
- **A server uninstalled is not unfollowed.** The stale row is correct in every case — the guild
  hears nothing about a server that no longer exists, hears nothing about the others it never
  followed, and hears about that name again if it is reinstalled. Dropping the row would empty a
  one-server list and silently switch that guild to following all of them.
- **A per-guild failure is logged and the rest proceed**, and the result counts guilds reached
  against the guilds that **follow this server**. A bare success with one guild silently missed is a
  fabricated status; counting a guild that opted out as missed is the same fault inverted, and would
  report a working filter as a partial failure on every announcement.
- **Joining a guild says one thing, once.** A guild with no row hears nothing by design, and from
  inside Discord that is indistinguishable from a broken bot — so `GuildGreeterService` posts an
  introduction on `JoinedGuild` naming `/setup` and who may run it. System channel, else the first
  channel it can actually post in, else the owner's DM, each **checked rather than attempted**; a
  guild that is already configured is not greeted, because that is a reconnection and it is already
  working. It grants nothing: `/setup` still needs KGSM admin.
- **Adopting a host that predates this**: `kgsm-bot --adopt-guild-config [--apply]` reads the old
  `Discord:GuildId` / `AnnouncementChannelId` / `InstancesCategoryId` and `KGSM:Instances` and
  writes the store. Dry-run by default, `--from <settings.json>` names the file to read (the
  shipped one no longer carries the map), `--announce-channel <id>` supplies the guild's channel
  when the old configuration left it at zero. **It refuses a guild that already has a row** rather
  than merging — a second run that re-pointed bindings is how live channels get orphaned and
  duplicated beside fresh ones, splitting every server's history in two.

The old `KGSM:Instances` map could not be delivered by environment variable at all — systemd drops
a variable whose name contains a hyphen, and an instance may be called `minecraft-homestead`. In a
database that constraint does not exist: it is a row.

## Architecture

Clean Architecture, four projects + tests. The dependency direction is
`Discord → Infrastructure → Application → Core`; Core depends only on `kgsm-lib`
models + logging abstractions.

- **`KGSM.Bot.Core`** — interfaces (`IServerInstanceService`, `IKgsmStateCache`, …),
  the `Result`/`Result<T>` type, and `InvocationContext` (provenance, below).
- **`KGSM.Bot.Application`** — the **MediatR** layer: commands/queries (`ServerCommands.cs`,
  `ServerQueries.cs`), their handlers, and `ServerEventCoordinatorService` (wires kgsm
  events → Discord announcements + cache invalidation).
- **`KGSM.Bot.Infrastructure`** — implementations over `kgsm-lib`'s `IKgsmClient`
  (`KgsmServerInstanceService`, `KgsmServerEventHandler`, `WatchdogService`), the
  `KgsmStateCache`, config option types, and all DI wiring (`DependencyInjection.cs`).
- **`KGSM.Bot.Discord`** — the entrypoint (`Program.cs` host builder, `BotService`
  background service) and the two user surfaces below.

### Two surfaces, one execution path

Everything that mutates a server funnels through the **same MediatR pipeline**
(`IMediator.Send` → handler → `IServerInstanceService` → `kgsm-lib`), regardless of
which front end triggered it:

1. **Slash commands** (`Commands/*Module.cs`, Discord.Net `InteractionModuleBase`) —
   `/start`, `/stop`, `/list`, `/status`, `/supervision`, blueprints, `/setup`, etc. The full list is
   published, not written — see *The command manifest* below.
2. **Natural language** (`MessageHandler.cs` + `Infrastructure/Assistant/`) — triggered by
   @-mentioning the bot. The message goes to the **kgsm-assistant leaf** over its HTTP
   surface, carrying the asking human's identity and the tier resolved from their roles. The
   bot runs no model and holds no conversation: the assistant's tool catalog is what acts,
   through its own kgsm access. A new assistant capability reaches Discord with no change
   here.

### Destructive ops: stage → confirm (model-independent gate)

The assistant never executes an action inside a turn — it **stages** one and returns an opaque
grant, which `MessageHandler` renders as Discord Confirm/Cancel buttons
(`Commands/AssistantConfirmationIds.cs`). The button carries the grant and nothing else, so **the
bot holds no part of the pending action**: a restart on either side leaves a posted button working,
and one lifetime governs it (`Assistant:Confirmation:TtlSeconds`, the assistant's).

`Commands/AssistantConfirmationModule.cs` forwards whoever clicked and the tier they hold at that
moment. **The assistant is the gate**: it re-derives authority, re-validates the target against live
inventory, refuses a grant that is already redeemed, and refuses one belonging to somebody else.

**Only the person who asked can approve.** A conversation belongs to one person and so do the
actions in it — the same rule the Control Panel follows. The bot's own operator check in front of
the call is a courtesy to the clicker, never the gate, and a refusal leaves the prompt standing for
whoever is permitted.

The Cancel id lives **outside** the confirm prefix: the confirm handler matches `kgsmact~*` on a
wildcard, and a cancel id underneath it would be captured and read as a grant.

### Authorization

**A Discord account says who you are; the KGSM account it is connected to says what you may do.** The
bot has no login of its own, so the Discord user the gateway names *is* the identity — and the tier is
whatever KGSM account that identity is a credential of, read from the host's account store
(`/var/lib/kgsm/auth/users.db`, `Auth:UsersDbPath`, `TheKrystalShip.KGSM.Auth.Users`). The Control
Panel and the assistant read the same record, so all three agree by construction rather than by each
deriving an answer.

**A guild role grants nothing, and neither does guild membership.** The gate is having an account
here, which an admin granted — strictly narrower than being in the Discord server, and the reason the
slash commands are safe registered globally. `/etc/kgsm/kgsm-auth.env` carries the sign-in
application and nothing else.

The store is opened **directly off the file**, not asked for over HTTP: a file cannot be down, so the
bot keeps authorizing people with every other leaf stopped. Reads are **uncached** — a point query
against a local file, at Discord typing speed — so an admin changing somebody's tier in the panel
lands on their very next command with no window at the old one.

`IKgsmAccounts.ResolveAsync` gives **four** answers, not a tier, and `AccountAnswer.Refusal` writes
the whole sentence for each so every surface refuses somebody in the same words:

| outcome | means | told |
|---|---|---|
| `Ok` | an account, usable; its tier is on the answer | — (or which tier it lacks) |
| `NotLinked` | no KGSM account has this Discord account connected | how to connect it |
| `Disabled` | the account was switched off | that it is disabled |
| `Unreadable` | the store could not be read | that nothing is known, and nothing was done |

`Unreadable` is deliberately not a denial. *"We could not ask"* is a different fact from *"the answer
is no"*, and reporting the first as the second demotes an admin mid-incident.

Every surface is gated:

- **Slash commands.** Each module carries `[RequireTier(KgsmTier.Viewer)]`, so a command from an
  account this host does not have is refused rather than answered. `[Mutating]` **is** the operator
  gate: it derives from `RequireTierAttribute`, so the attribute that puts a command in the panel's
  "acts" column is the same one that decides who may run it, and a new mutating command cannot be
  added and left open by forgetting a second attribute. `InteractionHandler` prints the precondition's
  reason **verbatim** — prefixing "you don't have permission" onto "your account isn't connected"
  states the one thing that is not true about it.
- **Natural language.** `MessageHandler` resolves the author's account and passes `canPerformActions`
  to the assistant; asking a question needs viewer, and someone with no account gets the same
  explanation rather than silence.
- **Confirm buttons.** `ConfirmationModule` **re-resolves at the click** rather than trusting the
  staging turn — the account someone held when the button was posted is not necessarily the one they
  hold now. It authorizes inline rather than by precondition, because a refusal must leave the prompt
  standing for whoever *is* permitted instead of failing the interaction.

`CommandManifestTests` pins all of it: the manifest's `gate` must equal what the modules enforce, every
mutating command must require operator, and every slash module must require an account.

### Provenance (who did what, attributable to kgsm)

`Core/Common/InvocationContext.cs` is an `AsyncLocal`-backed ambient holder. Entry
points call `_invocation.Begin(Invocation.ForDiscordUser(username))`; the kgsm
chokepoint (`KgsmServerInstanceService`) reads `Current` and stamps
`actor`/`origin` (`discord:<user>` / `discord`) onto every mutating `kgsm-lib` call,
so the engine's audit events are attributable. Outside any scope `Current` is null and
kgsm applies its **honest OS-user fallback — never a fabricated identity**. When adding
a new mutating entry point, wrap it in a `Begin(...)` scope.

### The command manifest: the Control Panel's list comes from the binary

`deploy/kgsm-bot.commands.json` is the catalog the Control Panel shows on this leaf's **Commands**
tab. It is **generated, not written**: an `AfterTargets="Build"` target runs the binary it just
produced with `--emit-commands`, and `Commands/CommandManifest.cs` reflects over this assembly's own
`InteractionModuleBase` types. So the file cannot name a command that does not exist, and a rename or
a new option reaches the panel with no second edit. Commit what the build produces.

- **A shipped file, not an endpoint**, for the same reason the config descriptor is one: this bot has
  no listening surface, and the list is wanted when the unit is stopped. `deploy.sh` installs it into
  `/var/lib/kgsm/leaves/commands/bot.json` — a subdirectory, because the descriptor scan globs `*.json`
  at the level above and would read it as a malformed descriptor. Format:
  `../leaf-command-manifest.md`.
- **The bucket is the tier the command is actually refused below** — its own `RequireTier`, else its
  module's, taking the higher of the two because Discord.Net evaluates both. So the panel prints the
  word the precondition enforces rather than one derived from it, and `/setup` lands in `admin` on
  the strength of changing no server at all.
- **`[Mutating]` is the one thing reflection cannot see.** It marks a command that changes something,
  which is what splits the panel into what reads and what acts. It *is* `RequireTier(Operator)`, so
  the attribute that puts a command in the "acts" column is the one that gates it.
  `CommandManifestTests` pins the set by name, so a new acting command that is not marked fails the
  build rather than being listed to an operator as read-only.
- **The operator bucket is not only the acting commands.** A command can need operator without
  changing anything — `/logs` and `/health` both show the inside of the machine — so the test asserts the direction
  that protects (everything marked `[Mutating]` is gated at operator) and names the reads that sit
  there, rather than asserting the reverse and forcing a read to be mislabelled as an action.
- **The tests compare against Discord.Net itself** — the same `InteractionService.AddModulesAsync`
  scan the bot runs at startup, command for command and option for option. The manifest is read by a
  process that never talks to Discord, so agreeing with what is actually registered is the only thing
  keeping it true.

### Announcements: the catalog is the journal's, and the operator owns each switch

The bot announces seventeen kinds of engine event, and `Discord:Announce` carries a switch per
kind that the Control Panel renders as its own section. Three rules hold the surface together:

- **A kind exists only if the journal carries it.** Nothing is polled, derived or inferred to
  fill a gap in the catalog — the bot never reaches into another leaf for a fact the engine does
  not emit. "Update available" is a kind because the engine emits `instance_update_available`:
  kgsm records what each update check found and emits only for a version it has not announced
  before, so a channel sees one message per new build however often the host checks. How often it
  checks is the scheduler's business, and nothing here.
- **The reduction happens where the payload's type is still known.** `KgsmServerEventHandler`
  turns each event into a `ServerAnnouncement` — kind, instance, one rendered detail, the actor
  verbatim — so nothing downstream switches over a kgsm-lib event class, and the announcement
  type does not grow a nullable field per event.
- **Announcing is not bookkeeping.** Creating a channel on install and retiring it on uninstall
  happen whether or not anything is announced, so they keep their own registrations. Install
  announces *after* its channel exists; uninstall announces *before* its channel is taken away.

**An announcement about a server that is down is something you act from.** `AnnouncementActions`
owns which ones, and the two sets are deliberately different. A **restart button** goes on
`instance_failed` only — a crash announcement says the supervisor is already restarting it, so a
button there races the supervisor over the same server and blames whoever pressed it for the attempt
that loses. A **thread** opens on both crash kinds, so the conversation about an incident stays with
it; the @-mention surface keys on the channel, which makes a thread its own context rather than one
more voice in the channel's. The button grants nothing: it is a shortcut to `/restart`, re-resolved
against the account store **at the click** (an announcement has no caller to authorize at the post)
and stamped with the clicker's provenance. `Discord:ActionButtons` and `Discord:IncidentThreads`
switch each off; a missing `Create Public Threads` costs the thread and nothing else.

**Crashes are announced once per crash, not once per restart attempt.** The supervisor emits an
`instance_crashed` per attempt, so a restart loop produces a run of them seconds apart. The
first attempt is the news; the outcome arrives separately as `instance_failed`. An unreadable
restart count is announced rather than dropped.

A server with no channel of its own in a guild — every server in a guild running no board, and one
installed before a board was turned on — reports in that guild's announcement channel. Which is why
that channel is the required half of `/setup` and the board is not. See *Where it announces*.

The switches, the status markers and the two message-cleanup keys are **host policy, not per-guild**:
what this host announces is its own business, and only where each announcement lands is a guild's.
Splitting them per guild is deferred until a second guild actually wants a different set.

### `/players`: who is on, and the four ways of answering it

`IPlayerRoster` is **the one place a player count comes from** — `/players` and the live status
message both read it, because two derivations are two numbers that can disagree in front of the same
person. It joins three facts from two authorities: run state from the engine, and both observability
and the live sessions from the supervisor (`IWatchdogClient.GetPlayerPresenceAsync`).

- **`RosterKnowledge` has one measured state and three refusals**, and `Count` is null for all three.
  A caller reads `Count`, never `Players.Count` — the latter is 0 in every state and would quietly
  turn "nobody can tell" into "nobody is here". `Known` is the only zero worth printing.
- **Stopped is decided before observability.** A stopped server has nobody on it whatever a stale
  session map holds, and that is a real answer to give even about a game this host can never see
  into.
- **A run state that could not be read is not a stopped server**, so a failed check falls through to
  whatever presence can say rather than short-cutting to `Stopped`.
- **Whether a game reports its players is the supervisor's answer, never derived here.** The
  predicate spans log patterns, RCON, and whether each pattern *compiles*; a surface deriving it from
  the instance's regex fields calls every RCON-polled game unknowable while its roster is being read.
- **An unnamed session is counted but not labelled.** The network address is deliberately not a
  fallback label: it identifies a connection rather than a person, and putting one in a chat message
  publishes a player's IP to the channel.
- **The run state it read is handed back on the answer** (`ServerRoster.Running`, null where the
  engine could not be asked). Deciding what a roster means costs a kgsm process per server, and the
  board and the presence both want that fact as well — asking twice would pay twice for one thing and
  could return two answers about the same moment. Nothing else in the bot calls `IsActiveAsync` per
  server to build a picture of the host — the board and the presence both read the roster. (`/list`
  still checks each server itself; it answers one person and is not joined to anything.)

### The bot's presence: the one thing it says with no channel to say it in

`Watching 6 servers · 3 online · 12 playing`, on the bot itself. It reaches a guild that has never
run `/setup`, and one update covers every guild at once, which no other surface here can do.

- **A gateway presence update is not REST, and `IDiscordSendQueue` does not pace it.** The limit is a
  handful per twenty seconds for the whole session. `PresenceRefreshSeconds` is the only thing
  protecting that budget, which is why the line is recomposed on a **fixed tick and never driven by
  an event** — a host reboot must not be able to spend it — and is sent only when it changed.
- **It never claims more than was read.** A host that could not be read says so instead of showing
  the last good numbers; an incomplete count is written as a floor (`3+ online`, `12+ playing`); and
  "0 playing" is never said, because it is noise on a quiet host and a lie on one whose games report
  nobody. The inventory is read separately from the roster for exactly one reason: an empty roster
  means both "no servers" and "could not be read", and those are opposite things to show somebody.

### `/logs`: the tail, as a file, to the person who asked

Operator-gated, and **ephemeral — which is a privacy decision, not a tidiness one**. A game server's
log routinely carries the network address of everyone who connected; this bot already refuses to put
an address in a roster for that reason, and posting the raw log into the channel would publish the
same thing with more of it. A file rather than a code block because Discord truncates a long one and
wraps every line of it on a phone. Oversized logs are trimmed **from the front** — the end is the part
somebody asked for — and the budget is counted in bytes, since that is what Discord's limit is in.

### Backups: what was captured, and how good it is

`/backups <server>` lists them, `/backup <server>` takes one, `/restore <server> [backup]` rolls one
back. `IBackupInsight` is the one place a backup fact comes from, for the same reason `IPlayerRoster`
is for players.

- **Consistency is measured per backup and is the thing worth reading.** The engine records how the
  capture was taken: `cold` (stopped — nothing could write mid-archive), `flushed` (running, but it
  wrote its world out first), `hot` (running with no usable save command — **the archive may be
  torn**), or nothing at all when the run state could not be read. ⚠ A surface that flattens those
  into "backed up ✅" hides the only part that decides whether the backup is worth having. **An
  unrecognised value is printed as it came** — the engine owns this vocabulary, and a surface guessing
  what a new word means is how a torn archive gets described as a good one.
- **Nothing stores an age; the timestamp is stored and the age computed at render.** That is what
  makes the whole-host summary cacheable — a cached timestamp still yields a correct age, where a
  cached age would silently stop counting. The cache is dropped by the engine's own
  `backup created`/`backup restored` events; the TTL is a backstop.
- **A present key with a null value is "read, and has none"; an absent key is "could not look".**
  `LatestAsync` distinguishes them and a renderer must too, and a failed read is deliberately not
  cached.
- **The board flags backups only when they are worth flagging** — past `BackupStaleAfterHours` (48h),
  or never taken at all. An age printed beside all sixteen servers buries the one that matters among
  fifteen that do not.
- **A restore is staged, and the button carries a handle.** A server name and a backup id together do
  not reliably fit a 100-character `customId`, and a truncated one names a *different archive* rather
  than failing — so the operation is held in `IStagedRestores` and the button carries 32 hex
  characters, the same shape the assistant's confirmations use. In memory and five minutes: a
  destructive action that survives a restart is one somebody clicks by accident days later.
- **Confirming is authorized at the click *and* restricted to the person who proposed it.** A restart
  button is a shortcut to a command anyone with the tier could type, so anyone with the tier may press
  it; this one names a specific archive somebody else chose. The handle is **peeked before it is
  redeemed**, so a click that is not allowed leaves the proposal standing for whoever is. Cancelling
  is open to anyone — the asymmetry is deliberate, one direction destroys and the other does nothing.

### `/history`: what happened, out of the durable record

`IServerHistory` wraps kgsm-lib's `IEventJournalHistory`, and `/history [server] [hours]` renders it —
viewer-gated, one server or the whole host. It reads **every producer's journal merged into one
time-ordered page**, the same set the announcements tail: a history that could not show the crash the
channel just reported would be the more confusing of the two failures. **This is a different reader
from the announcement one and
does not conflict with the no-cursor rule**: that rule is about the tail, which stores no position
because an announcement is only meaningful while it is current. A query answers a question somebody
just asked, and reaching back over a restart is exactly what it is for.

- **Three signals qualify every answer, and all three are carried across unflattened.** An
  **unreadable** journal is not a quiet host — they are the same empty list, and reporting the first
  as the second tells somebody nothing happened on the strength of a permission error. **Coverage** is
  the oldest moment the record still holds, said only when the window asked for more than that.
  **Truncation** is a scan that stopped at its budget, so the page is a prefix. The one empty answer
  that means *nothing happened* is the one with a readable journal behind it.
- ⚠ **The engine emits far more event types than the bot announces, and an unrecognised one is never
  dropped.** A measured day here carries deploy phases, UPnP forwards, port openings and prune results
  with no announcement kind behind any of them. The types worth naming are named; everything else
  renders from the engine's own word with its subject prefix stripped (`instance_deploy_finished` →
  "deploy finished"). That is what makes the phrase table safe to leave incomplete — a type added
  upstream still appears, still names its server and its actor, with no change here.
- **One field is lifted verbatim off each payload**, chosen by a documented order so an event carrying
  several is described by the most specific it has. Nothing is computed, and an event carrying none of
  them shows no detail rather than a stand-in.
- ⚠ **What a payload field *is* comes from kgsm-lib's `KgsmEventCatalog`; which of them says most
  about the moment is this surface's judgement.** The bot holds no second opinion about which fields
  identify somebody — it prints what the engine classifies as public and scalar, so a field
  reclassified upstream changes what Discord shows on the day the pin moves. That rule is what keeps
  four kinds of value off a line: a player's **network address** (personal — the same refusal the
  roster makes), **console input** verbatim (privileged, and this surface answers a viewer), a
  moderation **target** (the event does not say whether it is a name or an address — only the game's
  blueprint does, and a consumer that cannot tell treats it as personal), and **ports**, which are
  structured and already have a renderer on `/connect` that a second could disagree with. The events
  themselves still appear, so *that* each happened is never hidden.
- **The steps inside an operation are not listed beside it.** An install brackets its work with a
  dozen events around the one that is the news, and a list showing all of them buries the day in its
  own scaffolding — a measured 17% of two real days here. Which events are steps is the catalog's
  answer, carried on each moment, and **an unrecognised type is news**, so a type the engine starts
  emitting still appears. A failure is always news, whatever step it happened inside. The footer says
  how many steps were left out, because a filtered list presented as the whole window is the one thing
  this must not look like.
- The list is capped both by line count and by the embed's own character budget, counted as each line
  is added, and the footer says how many of how many were shown.

### `/health`: the failure systemd cannot see

Operator-gated, always private. The unit is active, the gateway says Connected, and the bot cannot do
the thing somebody just asked it about — an unreadable account store refuses every command, a missing
engine answers none of them, and neither shows up as anything but a process that is running.
`/setup show` answers a different question (what *this guild* is configured with), and the status
socket answers the Control Panel, which is no use to somebody who only has Discord.

`IBotHealth` runs seven checks at the moment they are reported: the gateway, the outbound queue, the
engine, the event journal, the KGSM account store, the guild store and the assistant.

- **No check is inferred from another.** They fail independently — the engine and Discord have nothing
  to do with each other — so a summary that took one as evidence for the next would report a state
  that was never measured. A check that throws is a failing check carrying the exception's words;
  nothing here may propagate, because the whole point is to be answerable while things are broken.
- ⚠ **Four verdicts, not two.** A dependency this host was never given is `Off`, not broken —
  counting an undeployed assistant against the total makes a correct host read as permanently short of
  something. A check that reached no answer is `Unknown`, not a pass, and is deliberately not green: a
  gateway mid-reconnect is neither connected nor faulty, and reporting it either way sends somebody
  the wrong place.
- **The engine is probed by asking it**, not by reading the inventory cache. A cache serves its last
  good answer for as long as its TTL says to, which is right for a cache and wrong for a health check.
  It costs one kgsm process, which is why a person runs this and nothing runs it on a timer.
- **The journal's readability and the age of its newest entry are two facts and stay separate.** A
  quiet host is not a broken one; inferring a fault from silence would call every idle weekend an
  outage.
- It overlaps the status socket on three facts (the gateway, the store, the queue) and **cannot
  disagree with it**, because both read the same live objects rather than either deriving from the
  other. Nothing is cached or held between calls.

### Voice: hear a room, answer out loud

`/voice join|leave|status` at operator (not `[Mutating]` — the gate is on what it exposes, since
everybody in the channel is heard, not only whoever invited it). `Discord:Voice` is off by default.
The path is **hear → recognise → match the trigger → assistant → speak**, and the pieces are
deliberately separable: capture knows nothing about recognition, and recognition nothing about who
answers.

- **DAVE is not optional.** Discord refuses a voice connection from a client that cannot negotiate its
  MLS encryption (close code 4017). `libdave` is packaged in `packaging/libdave/`, and
  `EnableVoiceDaveEncryption` plus `GuildVoiceStates` are required. Identity and segmentation come
  free — streams are keyed by Discord account id — so there is no diarization and no echo problem.
- **Answering must not run on the audio path.** A turn takes seconds, and run inside the tick that
  closes utterances it froze every other speaker's sentence for the whole time. `VoiceCommandQueue` is
  the handoff, and it is also what makes the wiring acyclic: session → sink → handler → session is a
  circle the container refuses.
- **The silence that ends a sentence has to be looked for.** Frames stop arriving when somebody stops
  talking, so the read loop cannot notice the gap it is sitting in — a ticker closes utterances.
- ⚠ **The trigger is matched anywhere in an utterance, not at the start.** Requiring it first was
  measured refusing real requests: people lead in ("okay let me try — hey assistant, …") and that is
  one breath, therefore one utterance. Accepted cost: quoting the phrase fires it.
- **The listening state is two tones, and it is a state rather than a message.** Waiting for you to
  speak and having taken your request are the surface's only two contentless moments, so they are
  marked by `VoiceChimes` — rising to open, falling to close, the same two notes reversed, which is
  the convention every device already teaches. A tone costs no synthesis, so it arrives immediately,
  and it does not wear out the way a fixed phrase does. **Anything with something to *tell* you stays
  spoken**: a tone cannot say why, and a rising tone after a confirmation that could not be made out
  reads as "go ahead" when the opposite happened.
- ⚠ **A sentence's opening is read before it is finished, and that reading may only make a sound.**
  Recognition runs on a *closed* utterance, so being addressed could otherwise only be known after the
  speaker stopped — putting the "go ahead" tone after the words it was meant to encourage.
  `UtteranceAssembler.Peek` hands out a copy at `EarlyTriggerMs`, and `RecognisingUtteranceSink`
  matches the trigger in it. **Nothing is dispatched, counted, or opened from a partial**: it is half
  an instruction, and the complete copy arrives moments later. That is exactly what makes it safe for
  whisper to be wrong about a fragment. The one thing it may do besides sounding a tone is **stop an
  answer being spoken** — that undoes nothing, since the reply is in the chat and the turn behind it
  has finished, and being slow about it is the whole failure. It is **skipped, never queued**, when the recogniser is busy
  (`TranscribeIfIdleAsync`) — a look ahead at an unfinished sentence must never delay a finished one —
  and it costs a full recognition pass on most of what a room says, since whisper pads to a fixed
  window. `0` turns it off.
- **The tone player is its own seam.** The recogniser plays tones and is a dependency of the session
  that owns the connection, so `IVoiceChimes` resolves the session on first use rather than taking it
  in a constructor — the same circle `VoiceCommandQueue` exists to break. The same tone twice within
  two seconds is played once, because the trigger spotted early and the same trigger read again on
  close are both correct and the room only needs to hear it once.
- **The recogniser is primed with this host's names** (`SpokenVocabulary`), because whisper knows
  English and not that a server is called `Ketchup`. Nothing downstream rewrites what was said — see
  the CHANGELOG for why correcting a misheard name afterwards is not merely risky but unachievable at
  any threshold.
- **What is spoken is the whole reply, markup stripped — never a summary and never cut short.** A
  surface that rewords a reply on its way to being read out says things the assistant did not, and
  nothing in the channel would show it happened; one that stops part-way has decided somebody heard
  enough, and leaves the answer in the room disagreeing with the answer in the channel. **How long a
  reply runs is the assistant's to control** — a spoken turn asks for one written to be heard
  (`ReplyStyle.Voice`), and that is the lever, not a cap on this side.
- ⚠ **The trigger cuts the bot off, and nothing weaker does.** Saying it while an answer is playing
  stops that answer (`IVoiceSessions.StopSpeaking`, `Voice:Interruptible`), and the request that
  stopped it is answered next. Receiving never pauses for speaking — they are separate directions on
  one connection — so the cheap signal, *somebody started talking*, is already in the pipe at ~20ms
  against the ~1.5s a trigger match costs. It is deliberately not used: a voice channel is a room
  where people talk over each other, and acting on it silences the bot every time two of them do.
  Continuing a conversation is not cutting into one, so an answer inside a `VoiceAttention` window and
  the rest of a request truncated at the ceiling both leave the speech alone.
- ⚠ **Stopping the write does not stop the sound — the stream has to go.** Up to `BufferMillis` of
  audio is already queued in the writer and plays on regardless, so an interrupt disposes `_out` and
  the next answer builds a new one. The writer's own `ClearAsync` **cannot** be used: it dequeues
  frames without releasing the queue slots or returning their buffers to the pool, so one call starves
  the stream for good. Every path that abandons the stream disposes it, because a `BufferedWriteStream`
  owns a send loop that runs until something cancels it — a dropped reference is a second writer on the
  same connection as its replacement.
- ⚠ **Clearing or compacting a conversation is read BEFORE a turn exists.** `/conversation clear` and
  `/conversation compact`, and spoken ("start over", "forget everything") via
  `SpokenConversationCommands` → `IAssistantTurnClient.RunCommandAsync` → the assistant's own
  `/commands/{name}`. These act on the stored conversation, so they can never be a question: a model
  told to forget replies that it has and remembers every word. The phrase list is matched
  deterministically against the **whole utterance** — containment would turn *"the server didn't start
  over the weekend"* into a wipe — and the assistant owns what each command does and who may run it,
  including the operator gate on clearing a shared room. Its wording is shown and spoken verbatim;
  nothing here forms a second opinion about what happened.
- ⚠ **A staged action is never approved out loud.** It is offered with the same buttons the @-mention
  surface posts and the spoken reply says so. The button re-derives authority at the click; a
  recogniser cannot, and a spoken yes would be a second way to authorise a destructive action.
- ⚠ **Audio shorter than the output buffer is padded to it, and every write is bounded.** Discord.Net's
  buffered writer transmits nothing until its queue holds a full buffer's worth of frames — below that
  its send loop waits forever and the flush waits on the send loop, so a short write neither returns
  nor throws. Measured: one 290ms tone wedged the stream and, because requests are answered one at a
  time, silenced the whole surface with nothing in the log. `SendableAudio` owns the floor and the
  buffer length together, since the two disagreeing is the cause. A short **spoken** answer ("Yes.")
  hits this too — it is a property of the writer, not of tones.
- **Speaking is best-effort throughout.** No model, no card, or a broken output stream costs the audio
  and nothing else — the answer is already in the channel.
- **Nothing is written to disk.** An utterance is bytes in memory handed to a sink and released. This
  is a bot that hears a room, not one that records it, and the difference is structural rather than a
  setting. `LogTranscripts` is the one exception, opt-in, and warns while it is on.

### Read commands answer one person

`/status`, `/list`, `/is-active` and `/supervision` reply ephemerally under `EphemeralReads` (on) — a
busy channel does not need everyone's status checks in its scrollback. `/history` follows the same
switch. ⚠ **`/connect` is deliberately not one of them**: its whole purpose is to be read by somebody
other than the person who typed it. `/logs` and `/health` are always private whatever the switch says
— a game log carries player IP addresses, and a failing health check names host paths and the reasons
stores could not be opened.

### One queue out to Discord

**Everything the bot says unprompted goes through `IDiscordSendQueue`.** Announcements fanning out
across guilds, the status board's per-guild edits, channels created and retired with an install, and
expiring messages cleaned up are four producers with no knowledge of each other, each able to burst.
Rate-limit headroom is a host-wide resource, and being throttled off the API loses everything else
with whichever call spent the last of it — the same failure that makes run state in a channel name
unbuildable. One worker in front of all of it makes them one paced stream. **A new outbound call
belongs here too; a direct `SendMessageAsync` off the client is a producer nothing paces.**

- **The floor is the mechanism, the backoff is the backstop.** `SendQueueMinIntervalMs` keeps a limit
  from being reached at all, which is worth more than any recovery — a 429 has already spent the
  request that earned it. When one arrives anyway the hold-off pauses the **whole** queue and doubles
  to `SendQueueMaxBackoffMs`, because one call spinning against a limit while everything else waits
  behind it is the failure this exists to prevent. Discord.Net still owns per-bucket waiting: it reads
  the rate-limit headers, which is better information than anything here has.
- **Only a rate limit, a server error or a dropped connection is re-tried.** A 403, a 404 or a
  malformed request is the answer, not a hiccup; re-asking spins against a permission that is not
  coming back, and for anything that posts it risks a duplicate. Classification defaults to *not*
  transient — an unrecognised failure retried is a call made twice more for nothing.
- **A full lane refuses and says so.** An unbounded queue in front of a rate limit is a memory leak
  with a delay on it, and every message in the backlog is staler than the last. Overflow returns a
  failure the caller reports, so its own accounting shows the guild it did not reach: a silent drop
  makes a bot that announces nothing look like a host where nothing happened.
- **Two lanes, and the split is about which one still reads correctly late.** An announcement, the
  thread under it and its buttons are what somebody is waiting for. A board republish, a pin, an
  expiring message's deletion and channel management are correct whenever they land, and the next
  tick would have refreshed the board regardless.
- **Interaction replies are not in it, and must not be.** Discord gives three seconds to acknowledge
  an interaction; a reply queued behind a backlog arrives after the token is dead. Somebody waiting on
  their own slash command is also not the traffic that causes a throttle.
- **A failed send is a `Result`, never an exception** — one guild's dead channel cannot unwind the
  loop over the others. `SendAsync<T>` cannot carry a null success (`Result<T>` forbids one), so a
  call that can answer "there is no such thing" uses the non-generic overload and captures the value.
- The backlog is on the status socket (`sendQueue`). Connected, configured, every channel visible and
  messages arriving minutes late is a real state whose only symptom is a depth that does not fall.

### State cache & events

`KgsmStateCache` caches the instance/blueprint inventory (TTL backstop +
event-driven invalidation) so the bot doesn't spawn a `kgsm` subprocess per message;
on a refresh failure it **serves the last-known-good snapshot rather than blanking**.
**The bot tails every producer's journal, not the engine's alone** — a file each has, which any
number of consumers read, so nothing is bound and nothing on any producer's side names this reader.
`KGSM.JournalDir` names the *engine's*, whose location is configurable; the rest are found on disk.
Those events drive both the announcements and cache invalidation via `ServerEventCoordinatorService`.

⚠ **Six of the seventeen announced kinds are not the engine's to emit.** `instance_crashed`,
`instance_failed`, `instance_started`, `instance_ready`, `instance_restarted` and player presence are
the **supervisor's**, in its own journal. `AddKgsmJournalFederation` must therefore stay registered
**after** `AddKgsmServices` in `DependencyInjection.cs` — above it, the single-journal registration
wins, nothing throws and nothing is logged, and this bot announces installs and backups perfectly
while going silent about every incident. `JournalFederationWiringTests` pins both halves, because
that failure has no symptom from inside a Discord channel.

**It starts at the tail and stores no position.** This surface *announces*, and an
announcement is only meaningful while it is current — replaying a backlog after a restart
would post "server started" for a server that started and stopped hours ago. The federated source
keeps one position **per producer**, so a cursor here would replay each journal independently and
post a morning's crashes at once. Don't give this consumer a cursor to "avoid missing events": the
durable record is the journals themselves, and missing a restart window here costs nothing.

## Ecosystem invariants that bite here

- **Never fabricate a status.** An unreadable instance is reported as unavailable with the
  reason, never as a fake `stopped`. A confirmed action reports the assistant's *watched*
  verdict — "the engine accepted it" and "the server got there" are different claims, and a
  run state that could not be read is reported as unread.
- **Degrade gracefully when a dependency is absent.** The watchdog is optional: an
  unreachable daemon makes `/supervision` report "unavailable" while native start/stop still
  works. The assistant is optional too — unconfigured or unreachable, the @-mention surface
  says so and goes quiet while slash commands, announcements and channel status carry on.
  There is deliberately **no fallback engine**: answering from a second one would split a
  person's history across two memories exactly when things are going wrong.
  The **firewall authority** is optional in the same way, and read-only from here: it answers
  `/connect`'s "can you actually reach it", and an unreachable one costs that one line. Ports open
  when a server starts and close when it stops — the watchdog's and the authority's business, never
  a chat surface's.

## `/connect`: the question a game Discord actually asks

`ServerConnectionService` composes three sources that fail independently — the engine (the instance
and its `Ports`, already canonical `[{start,end,protocol}]`), the host (`HostAddressService`), and
the firewall authority (`FirewallReport`) — and a failure of one is reported on the piece it belongs
to. The ports are worth having when the external IP could not be read; the address is worth having
when no firewall answered.

- **An operator-set address wins over a measured one.** A host cannot discover the name people
  actually type — a DNS record pointing at it is a fact about the world, not about the machine — so
  `Discord:PublicAddress` is used verbatim. Blank, the host's measured external IP is the fallback,
  and the reply says it can change without notice. Neither answering is stated as not knowing.
- **`PortExposure` has three ways of not knowing and none of them is "closed".** The one that matters:
  a backend installed but **not enforcing** filters nothing, so its empty rule set means every port is
  reachable — `Unfiltered`, which is the opposite of what that set naively reads as. `Unknown` is the
  authority saying it cannot tell, `Unavailable` is no authority at all. kgsm-lib reports enforcement
  separately from the rules precisely so the distinction survives.
- **A server with no declared ports gets no connect string.** A guessed port is a wrong answer that
  looks like a right one.

## Tests

`tests/KGSM.Bot.Core.Tests` — xUnit + NSubstitute + FluentAssertions, mirroring the
source layout (`Application/`, `Infrastructure/`, `Discord/`, `Common/`). They mock at the
`IServerInstanceService`/`IMediator`/cache seams and at the assistant client's HTTP
transport; there is no live Discord, kgsm or assistant in the suite.

## Version tracking

- **Version source:** `<Version>` in `src/KGSM.Bot.Discord/KGSM.Bot.Discord.csproj` — the deployable; the Core/Application/Infrastructure libraries carry none
- **Packaging reads it via `deploy/version.sh`** — `./deploy/version.sh` prints the declared version, `--pkgver` prints the pacman-safe form. A package never restates a version number; it asks for one.
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.
