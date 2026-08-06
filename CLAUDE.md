# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-bot` is the **Discord surface onto KGSM** — one of the `kgsm-*` leaves in the
`tks` umbrella workspace. It is a **leaf consumer**: it reaches the engine only
through `kgsm-lib` (and the extracted assistant), never by shelling `kgsm.sh`
itself. Read the workspace root `../CLAUDE.md` + `../system-architecture.md` for the
ecosystem rules this repo inherits (the dependency spine, "never fabricate a
status", leaves-are-independently-deployable).

## Building requires the full umbrella checkout

This is the single biggest gotcha. The LLM projects are **ProjectReferenced by relative
path** — there is no NuGet fallback:

- `../../../kgsm-llm/TheKrystalShip.Llm/...` and `.../TheKrystalShip.Kgsm.Assistant/...` (the LLM agent loop + the extracted kgsm assistant)

So `kgsm-bot` only builds when `kgsm-llm/` is checked out as a sibling under `tks/`. A
standalone clone will not restore.

**kgsm-lib is different: it comes from the local feed** (`nuget.config` →
`/home/heisen/local-nuget`) as `TheKrystalShip.KGSM.Lib`, pinned by version in Core,
Application and Infrastructure. Editing `kgsm-lib/` changes nothing here until it is
repacked and the three pins move together. NuGet caches by id+version, so a repack at the
same version serves the old package from the cache with no error.

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
surface with its defaults, and is committed — it holds no secret and no host identity.
Key sections: `KgsmAuth` (the shared role map — see *Authorization*), `Discord` (token, `GuildId`,
`InstancesCategoryId`, `AnnouncementChannelId`, status markers, and the `Announce` switches),
`KGSM` (`Path` to `kgsm.sh`, `JournalDir`, `WatchdogSocketPath`, and the
`Blueprints`/`Instances` maps), `Ollama`/`Conversation`/`LlmAgent`/`Llm` (the assistant),
`KgsmCache` (inventory TTLs).

An environment variable **overrides one key** of that file by spelling the key's path with
`__` (`Discord__GuildId`), and a variable naming a key the file does not declare binds to
nothing. That is where the token and this host's own scalars live
(`/etc/kgsm-bot/kgsm-bot.env`). The descriptor generator fails the build when the settings
file and the annotated options classes disagree in either direction, so a key added to one
without the other never ships.

**`deploy/kgsm-bot.leaf.json` is generated, not written.** `TheKrystalShip.KGSM.LeafConfig`
rewrites it on every build from `[LeafField]` attributes and `<panel>` doc tags — so edit the
options classes, never the JSON, and commit what the build produces. `Discord`, `KGSM` and
`KgsmCache` carry theirs on their own types in `KGSM.Bot.Infrastructure`, which the generator
picks up because it scans every assembly beside the built binary. `Ollama`, `LlmAgent`,
`Conversation` and `KgsmAuth` are declared as `[LeafFrameworkField]`s in `LeafDescriptor.cs` instead:
those types belong to `TheKrystalShip.Llm` and `TheKrystalShip.KGSM.Auth`, and each surface describes
the same keys in its own words, so the prose has to live with the surface that shows it. Format:
`../leaf-config-descriptor.md`; mechanism: `../kgsm-leafconfig/README.md`.

**The `KGSM:Instances` channel map is the one host-specific thing that stays in the settings
file**, because systemd refuses an environment variable whose name contains a hyphen — an
instance called `minecraft-homestead` is dropped with *"Ignoring invalid environment
assignment"* and given a new channel on its next event. Nothing else persists that map, so a
server missing from it loses its channel history.

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
   `/start`, `/stop`, `/list`, `/status`, `/supervision`, blueprints, etc. The full list is
   published, not written — see *The command manifest* below.
2. **Natural-language LLM** (`MessageHandler.cs` + `Llm/`) — triggered by @-mentioning
   the bot. The message is run through the extracted assistant (`IServerAssistant`,
   Ollama-backed). `Llm/MediatorServerOperations` and `Llm/StateCacheInventory` are
   **adapters** that satisfy the assistant's `IServerOperations`/`IServerInventory`
   ports by re-dispatching onto the *same* MediatR handlers + state cache — so LLM and
   slash behaviour are identical. (Keep them in sync: a new bot action means a new
   MediatR command **and** wiring it into the adapter.)

### Destructive ops: stage → confirm (model-independent gate)

The LLM never executes install/uninstall/destructive ops directly — it **stages** a
`PendingConfirmation`, which `MessageHandler` renders as Discord Confirm/Cancel
buttons. Execution happens only on a human Confirm click, in
`Commands/ConfirmationModule.cs`, which **re-authorizes the clicker and re-validates
the target against the live list** (neither trusted from the staging turn). The button
`customId` encoding lives in `Llm/ConfirmationIds.cs` — the per-kind letter codes are
**append-only** (a posted button carries its old code until clicked; never reuse a
letter). Payloads too long for Discord's 100-char customId (e.g. a long SetConfig
value) are stashed server-side via `Llm/PendingEditStore`.

### Authorization

Authority comes from **`TheKrystalShip.KGSM.Auth`**, the ecosystem's shared role map, so a person gets
the same answer here as they do in the Control Panel and the assistant. The `KgsmAuth` section carries
the role ids and lives once per host, in `/etc/kgsm/discord-auth.env`, which this unit loads *before*
its own env file — so setting a role id in `kgsm-bot.env` overrides the shared value for this leaf
alone, which is how a host grants one person different authority on different surfaces. Guild
membership is the access gate and floors a member at **viewer**, and `KgsmAuth:RoleOperatorIds` /
`RoleAdminIds` elevate from there. Both lists empty leaves everyone at
viewer, so nothing acts until they are set.

The bot resolves the tier from **the member object the gateway already hands it** — no REST call, no
bot token for lookups, no cache. That is why this leaf takes only the model half of the shared auth
packages and none of the transport.

Every surface is gated:

- **Slash commands.** Each module carries `[RequireTier(KgsmTier.Viewer)]`, so a command run where
  there is no member object to read — a DM — is refused rather than answered. `[Mutating]` **is** the
  operator gate: it derives from `RequireTierAttribute`, so the attribute that puts a command in the
  panel's "acts" column is the same one that decides who may run it, and a new mutating command cannot
  be added and left open by forgetting a second attribute.
- **Natural language.** `MessageHandler` resolves the author's tier and passes `canPerformActions` to
  the assistant; reading stays open to any guild member.
- **Confirm buttons.** `ConfirmationModule` **re-resolves at the click** rather than trusting the
  staging turn — the roles someone held when the button was posted are not the roles they hold now. It
  authorizes inline rather than by precondition, because a refusal must leave the prompt standing for
  whoever *is* permitted instead of failing the interaction.

`CommandManifestTests` pins all of it: the manifest's `gate` must equal what the modules enforce, every
mutating command must require operator, and every slash module must require membership.

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
- **`[Mutating]` is the one thing reflection cannot see.** It marks a command that changes something,
  which is what splits the panel into what reads and what acts. `CommandManifestTests` pins the set by
  name, so a new acting command that is not marked fails the build rather than being listed to an
  operator as read-only.
- **The tests compare against Discord.Net itself** — the same `InteractionService.AddModulesAsync`
  scan the bot runs at startup, command for command and option for option. The manifest is read by a
  process that never talks to Discord, so agreeing with what is actually registered is the only thing
  keeping it true.

### Announcements: the catalog is the journal's, and the operator owns each switch

The bot announces sixteen kinds of engine event, and `Discord:Announce` carries a switch per
kind that the Control Panel renders as its own section. Three rules hold the surface together:

- **A kind exists only if the journal carries it.** Nothing is polled, derived or inferred to
  fill a gap in the catalog. This is why there is no "update available" announcement: the
  engine emits no such event, and the only honest source is kgsm-api's own update probe, which
  is not a Discord one and which no leaf may reach into.
- **The reduction happens where the payload's type is still known.** `KgsmServerEventHandler`
  turns each event into a `ServerAnnouncement` — kind, instance, one rendered detail, the actor
  verbatim — so nothing downstream switches over a kgsm-lib event class, and the announcement
  type does not grow a nullable field per event.
- **Announcing is not bookkeeping.** Creating a channel on install and retiring it on uninstall
  happen whether or not anything is announced, so they keep their own registrations. Install
  announces *after* its channel exists; uninstall announces *before* its channel is taken away.

**Crashes are announced once per crash, not once per restart attempt.** The supervisor emits an
`instance_crashed` per attempt, so a restart loop produces a run of them seconds apart. The
first attempt is the news; the outcome arrives separately as `instance_failed`. An unreadable
restart count is announced rather than dropped.

A server with no channel of its own — one predating the `KGSM:Instances` map — falls back to
`Discord:AnnouncementChannelId`, or is dropped with the reason logged when that is zero.

### State cache & events

`KgsmStateCache` caches the instance/blueprint inventory (TTL backstop +
event-driven invalidation) so the bot doesn't spawn a `kgsm` subprocess per message;
on a refresh failure it **serves the last-known-good snapshot rather than blanking**.
The bot tails the engine's event journal (`KGSM.JournalDir`) — a file every consumer
reads, so nothing is bound and nothing on the engine side names this reader. Those events
drive both the announcements and cache invalidation via `ServerEventCoordinatorService`.

**It starts at the tail and stores no position.** This surface *announces*, and an
announcement is only meaningful while it is current — replaying a backlog after a restart
would post "server started" for a server that started and stopped hours ago. Don't give
this consumer a cursor to "avoid missing events": the durable record is kgsm-monitor's,
and missing a restart window here costs nothing.

## Ecosystem invariants that bite here

- **Never fabricate a status.** Fleet status and health snapshots map an unreadable
  instance to an explicit `Unavailable`/reason, never a fake `stopped`
  (`MediatorServerOperations.GetFleetStatusAsync`, the `run_health_check` query).
- **Degrade gracefully when a dependency is absent.** The watchdog is optional: an
  unreachable daemon makes `/supervision` report "unavailable" while native
  start/stop still works. Assistant capabilities not yet wired on Discord
  (`view_config_file`, `list_files`) return a polite failure rather than throwing — the
  shared port stays satisfied.

## Tests

`tests/KGSM.Bot.Core.Tests` — xUnit + NSubstitute + FluentAssertions, mirroring the
source layout (`Application/`, `Infrastructure/`, `Llm/`, `Common/`). They mock at the
`IServerInstanceService`/`IMediator`/cache seams; there is no live Discord or kgsm in
the suite. `InternalsVisibleTo` exposes the internal `Llm/` adapters to the tests.

## Version tracking

- **Version source:** `<Version>` in `src/KGSM.Bot.Discord/KGSM.Bot.Discord.csproj`
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.
