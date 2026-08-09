# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Authority comes from the KGSM account store** (`Auth__UsersDbPath`, default
  `/var/lib/kgsm/auth/users.db`) instead of from a Discord guild role. A Discord account says who
  somebody is; the KGSM account it is connected to says what they may do — the same record the Control
  Panel and the assistant read, so all four surfaces now agree by construction rather than by each
  deriving an answer. Guild membership and guild roles grant nothing anywhere.

  **A Discord account connected to no KGSM account can no longer use the bot**, and is told so
  explicitly with how to connect it — never an opaque permission error. Everyone who has signed in to
  the Control Panel already has an account, and it is already connected.

  The refusal says which of four things happened: no account connected, an account switched off, a
  tier too low, or a store that could not be read. The last is deliberately not a denial — "we could
  not ask" is a different fact from "the answer is no", and reporting the first as the second would
  demote an admin mid-incident.

- Tracks `TheKrystalShip.KGSM.Auth` 2.0.0 and takes `TheKrystalShip.KGSM.Auth.Users` 1.2.0.
  `KgsmAuth` is now the sign-in application alone; `KgsmAuth__GuildId`, `BotToken`, `RoleAdminIds`
  and `RoleOperatorIds` bind to nothing and are gone from the settings file and the leaf descriptor.

### Added
- **A status socket** (`KGSM__StatusSocketPath`, default `/run/kgsm-bot/status.sock`) serving one JSON
  line per connection: gateway connection state and latency, the guild the client actually resolved,
  the instance→channel map with per-channel reachability, the registered command count, and every
  announcement switch. Same NDJSON-over-unix-socket shape kgsm-scheduler serves — a Discord bot carries
  no web stack, and there is one consumer.

  Reading that line is also the first real health signal this leaf has had. systemd liveness and the
  gateway's own state both read healthy in exactly the situation where the guild failed to populate and
  the bot can post nothing at all; the resolved guild is what distinguishes them.

### Changed — the command manifest is keyed by gate (schemaVersion 2)

`deploy/kgsm-bot.commands.json` groups commands under the gate that admits them rather than carrying
one leaf-wide `gate` beside a flat list. Commands that act sit under `operator` — the tier
`[Mutating]` already enforces — and commands that only read sit under `none`, the bot stating that it
checks nothing of its own for them beyond the guild membership every module requires.

Keying by gate means a command cannot be added without landing in a bucket, the same property that
makes `[Mutating]` both the mark and the gate. `CommandManifestTests` now holds the buckets against
what the modules enforce: nothing mutating outside `operator`, nothing read-only inside it.

Format: `../leaf-command-manifest.md`. kgsm-api reads both versions, so this needs no deploy ordering.

### Removed — the second assistant engine, and everything that served it

The bot no longer runs a model. `TheKrystalShip.Llm` and `TheKrystalShip.Kgsm.Assistant` are gone
from this repo along with the Ollama client, the agent loop, the conversation store, the parallel
`IServerOperations`/`IServerInventory` adapters, and the confirmation encoding they needed. There is
one engine behind Discord, the Control Panel and the assistant's own site, and it is the
kgsm-assistant leaf.

**⚠ Configuration removed: `Ollama`, `Conversation`, `LlmAgent` and `Llm`.** A host that still sets
any of them binds nothing. Their replacement is the `Assistant` section. The prompt text that lived
in `Llm:Preamble` / `ActionsAllowed` / `ActionsDenied` is **not migrated**: the assistant's own
prompts already say the same things and say them more accurately — every action stages behind a
confirmation now, which the bot's text predated. A host that wants a different Discord voice writes
`<Prompts:Directory>/kgsm-bot/preamble.md` on the assistant, which overrides per leaf.

**⚠ `/var/lib/kgsm-bot` is no longer created or used.** The bot keeps no state at all;
`setup.sh` provisions nothing and `deploy.sh` prunes the whole prefix. An existing directory is
left alone and can be deleted by hand — the conversations in it are superseded by the assistant's.

**This repo builds standalone.** Its longest-standing gotcha — four `ProjectReference`s by relative
path into a sibling checkout, with no NuGet fallback — is gone. Every dependency is a package, so a
clone with nothing beside it restores and builds.

The parallel tool surface goes with it, and with it the maintenance tax of implementing every
assistant capability twice. Discord gains the assistant's full catalog rather than the four
capabilities that were stubbed here with "not available on the Discord surface yet".

### Added — a staged action is confirmed from a Discord button, by the person who asked

An action the assistant stages is posted with Confirm/Cancel buttons. The button carries the
assistant's grant and nothing else — the bot holds no part of the pending action, so a restart of
either side leaves a posted button working, and there is one lifetime to reason about
(`Assistant:Confirmation:TtlSeconds`) rather than two to keep ordered.

**Only the person who asked can approve.** A conversation belongs to one person and so do the
actions in it, which is the rule the Control Panel already follows; a surface that let another
operator approve would be a way around it. A click is re-authorized at the moment it happens — the
roles someone held when the button was posted are not the roles they hold now — and a refusal leaves
the prompt standing for whoever is permitted. The assistant is the gate: it re-derives the clicker's
authority, re-validates the target against what exists now, refuses a grant that is not theirs, and
refuses one already redeemed.

The outcome reports what is actually known. The assistant separates "the engine accepted this" from
"the server got there", so a command that ran without arriving says so, and a state that could not
be read is reported as unread rather than as stopped.

### Added — the bot asks the kgsm-assistant leaf, so Discord shares one conversation

`Assistant:BaseUrl` + `Assistant:RelaySecret` point the @-mention surface at the kgsm-assistant
leaf. The bot forwards the asking human — their Discord id, their name, and the tier resolved from
the roles they hold right now — through the assistant's own relay package
(`TheKrystalShip.Kgsm.Assistant.Relay`), the same writer the Control Panel API forwards identity
with, so the two cannot come to spell a person's authority differently.

The channel a message was posted in is the conversation, sub-scoped under the asker's own memory.
A thread held in Discord is therefore the same thread the Control Panel and the assistant's own
site list for that person, and reaches nobody else's history.

There is no fallback and deliberately nothing to fall back to: answering from a second engine when
the first is unreachable would split one person's history across two memories exactly when things
are going wrong. Unconfigured or unreachable, the conversational surface says so — slash commands,
announcements and channel status are untouched.

### Changed — kgsm-lib 3.1.0

Up from 2.2.0. The engine event journal is now queried directly through the library
(`IEventJournalHistory`), which retires kgsm-monitor's event index — nothing here read that index, so
this repo only follows the pin.

Two breaking changes in the library reach this code. `IEventService.RegisterRawHandler` and
`IEventSource.EventReceived` carry an `EventPosition` alongside the envelope, because an event's
journal position is now its identity: it is unique by construction, so two identical events emitted
within one second are no longer collapsed the way a content hash collapsed them. The bot registers no raw handler, so only the test asserting that had to move.
`IInstanceService` gained the player-moderation verbs (`Kick`/`Ban`/`Unban`) back in 2.1.0, which
this repo skipped over.

### Changed

- **The role map moves to the shared `/etc/kgsm/discord-auth.env`**, loaded before `kgsm-bot.env`, so
  the bot, the Control Panel API and the assistant answer every authority question with the same
  values. Setting a role id in the per-leaf file still overrides it, deliberately.

### Changed — one gate, shared with the rest of the ecosystem

- **Authority comes from `TheKrystalShip.KGSM.Auth`.** The bot resolves a caller's tier from the
  ecosystem's shared role map, so a person gets the same answer here as in the Control Panel and the
  assistant. Roles are read off the member object the gateway already provides — no REST call, no
  lookup token, no cache — which is why this leaf takes only the model half of the shared auth
  packages.
- **⚠ Slash commands are gated.** `/start`, `/stop`, `/restart`, `/install` and `/uninstall` now
  require **operator** and refuse anyone below it; every slash module requires guild membership, so a
  command run in a DM is refused rather than answered. They previously ran for anyone Discord let
  invoke them.
- **`[Mutating]` is the gate.** It derives from `RequireTierAttribute`, so the attribute that puts a
  command in the panel's "acts" column is the same one that decides who may run it — a new mutating
  command cannot be added and left ungated by forgetting a second attribute.
- **The command manifest reports `gate: "operator"`**, and `CommandManifestTests` now fails unless the
  modules actually enforce what it claims.

### Removed

- **`Discord:ActionRoleId`.** Replaced by `KgsmAuth:RoleOperatorIds`, which is a list and is shared
  with the other surfaces. A host carrying only the old key leaves everyone at viewer, so nothing acts
  until the new one is set.

### Added — the Control Panel lists the bot's real commands

- **`deploy/kgsm-bot.commands.json`, generated from the built binary.** The Control Panel's leaf page
  for this bot now carries a **Commands** tab listing every slash command, what it does, and what it
  takes — split into what reads and what acts. The list is produced by the build running the binary it
  just made with `--emit-commands`, reflecting over this assembly's own `InteractionModuleBase` types,
  so it cannot name a command that does not exist and a rename reaches the panel with no second edit.
  `deploy.sh` installs it into `/var/lib/kgsm/leaves/commands/bot.json` — a subdirectory, because the
  config-descriptor scan globs `*.json` one level above and would read it there as a malformed
  descriptor. Format: `../leaf-command-manifest.md`.

- **`[Mutating]`** marks a command that changes something (`/start`, `/stop`, `/restart`, `/install`,
  `/uninstall`). It is the one fact reflection cannot see, and it is what splits the panel into the
  commands that read and the commands that act. `CommandManifestTests` pins the set by name, so a new
  acting command that is not marked fails the build rather than reaching an operator labelled
  read-only. The same tests compare the manifest against `InteractionService`'s own module scan —
  command for command, option for option, including each option's type, requiredness and autocomplete
  — because a file read by a process that never talks to Discord is only true while it agrees with
  what the bot actually registers.

- **The manifest states what the bot checks before acting, and today that is nothing.** `gate` is
  `none`: `Discord:ActionRoleId` gates the natural-language surface and the confirm buttons, and was
  never wired into the slash modules, so `/uninstall` runs for anyone the guild lets invoke it and the
  only restriction available is a per-command permission in Discord's own Integrations settings. The
  panel says so rather than leaving an operator to assume otherwise, and a test fails if a precondition
  is added without the manifest being told.

### Added — configurable event announcements

- **Fourteen announcement switches (`Discord:Announce`), rendered as their own Control Panel
  section.** The bot announces sixteen kinds of engine event — start, ready, stop, restart, crash,
  give-up, update, install, uninstall, backup created/restored, player join/leave, and the three
  moderation verbs — and each is switchable by the operator from the Control Panel. Previously the
  bot posted a bare status emoji for four of them with nothing to configure, and the Discord
  settings an operator could actually see belonged to kgsm-api's webhook integration, which is a
  different delivery path entirely.

  Every kind is sourced from an event the bot already reads off the journal. There is deliberately
  no "update available" switch: the engine emits no such event, so the bot has no source it could
  answer from without polling steamcmd per instance — kgsm-api's update probe remains the only
  honest source of that fact, and it is not a Discord one.

  `Ready`, `PlayerJoined`, `PlayerLeft` and `BackupCreated` ship off. The rest ship on.

- **`Discord:AnnouncementChannelId`** — where an announcement goes when its server has no channel of
  its own. A server only gets a channel when the bot sees it installed or finds it in the
  `KGSM:Instances` map, so anything predating that map routed nowhere and was dropped. Zero keeps
  the drop, and the reason is logged.

- **A crash is announced once per crash, not once per restart attempt.** The supervisor emits an
  `instance_crashed` per attempt, so a server in a restart loop produces a run of them seconds
  apart — measured on this host, four crashes produced eleven events. The first attempt is
  announced; the outcome, when the supervisor runs out of attempts, arrives as `instance_failed` and
  is announced in its own right. A restart count that cannot be read is announced rather than
  dropped.

- **Announcements name their detail and their actor** — an exit code, a version pair, a blueprint, a
  player — each taken from the event that carried it and left out when it did not. The actor travels
  verbatim: a supervisor-driven event reads as automatic, never as a person.

### Changed

- **`Discord:DeleteStatusMessageAfterDelay` defaults off, with a 300s lifetime when on.** It governs
  announcements, which are worth keeping; the old 2-second default deleted every one of them almost
  as soon as it was posted.
- **`TheKrystalShip.KGSM.Lib` 2.0.0 → 2.2.0**, for the three moderation event types.
- **`IServerEventHandler` carries one announcement subscription** instead of a registration method
  per event, with the install/uninstall pair kept separate because creating and retiring a channel
  has to happen whether or not anything is announced. Install announces after its channel exists;
  uninstall announces before its channel is taken away.

### Changed — the leaf config descriptor is generated, not written
- **`deploy/kgsm-bot.leaf.json` is now written by `TheKrystalShip.KGSM.LeafConfig` on every build**, from
  `[LeafField]` attributes and `<panel>` doc tags on `the bound options classes`. A knob lives in two places —
  the property and the settings-file key — instead of three, and the descriptor cannot describe a
  variable this leaf does not read: the `env` name is derived from the property's position under its
  bound section, and the default from the settings file itself. **Edit the settings class, not the
  JSON.**
- **A field's operator-facing prose comes from a `<panel>` tag**, falling back to `<summary>` with a
  build message naming the field. The two are separate because they answer different questions: the
  summary tells a developer what the value means to the code, the panel tells whoever runs the host
  what changing it does.
- **`LeafDescriptorTests` is gone.** Every check it made — settings coverage in both directions, the
  field vocabulary, group and `dependsOn` references, enum values and defaults, bounds, floor-source
  order — now runs in the generator, at the point the file is produced rather than after, and in one
  implementation shared by every leaf instead of a copy per repo.
- The package is **build-only** and declares no dependencies: the attributes arrive as source and the
  generator reads this assembly's metadata in its own process, so nothing reaches the published
  output and this leaf gains no reflection.

### Fixed — the bot could not resolve its own Discord server

- **`Discord__GuildId` binds.** The config file spelled the key `Discord:Guild`, which matches no
  property on `DiscordOptions`, so the guild id was **0** on every run — visible in the journal as
  *"Initializing server event coordinator for guild 0"*. With a guild of 0 the bot signs in and
  reports status normally, but it cannot resolve the server it belongs to: creating a channel for a
  newly installed game server fails with *"Could not find guild with ID 0"*, and removing one on
  uninstall fails the same way. Both work again.

### Changed — one settings file, and it declares the whole surface

- **`appsettings.json` is now `kgsm-bot.settings.json`**, matching the ecosystem's
  `kgsm-<leaf>.settings.json` naming — and it is **committed**, because it holds no secret. It
  declares the bot's whole configurable surface with its defaults, and `Program.cs` loads it by
  absolute path from the binary's own directory rather than the process working directory.
  `appsettings.example.json` is gone: with the real file committed, it was a second copy of the same
  declarations, and it had already drifted from both the code and the live file.
- **The token and this host's Discord identity moved to `/etc/kgsm-bot/kgsm-bot.env`** — the guild,
  the channel category and the action role, which are this host's and not the product's.
- **Nine settings the bot binds but the file never declared are now declared**, with the value the
  code already used: `KGSM__JournalDir`, `KGSM__WatchdogSocketPath`, `Ollama__Temperature`/`Seed`/
  `Think`, `Conversation__DatabasePath`, and the three `LlmAgent` knobs.
- **Four dead keys are gone**: `KGSM__SocketPath` (the engine moved to the event journal) and
  `Conversation__MaxMessages`/`IdleTimeoutMinutes` (the history is append-only canon, bounded by
  checkpoints rather than trimmed), plus the misspelled `Discord__Guild` above.
- **`floorSources` lists the settings file first.** The list is lowest-precedence-first, so with the
  file listed last the Control Panel resolved a knob to the file's value and reported it as the
  deployed one — showing a blank where the unit sets a real path.

### Added
- **Four tests hold the settings file, the bound options classes and the leaf descriptor together**:
  a key in the file that binds to nothing, a bound setting the file never declares, a descriptor
  default that disagrees with the file, and an env template naming a key the file does not declare
  each fail the build. The first two are what would have caught the guild id.

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. This service already read the
  journal, so the only change here is dropping the now-nonexistent `EventTransport = Journal` line —
  there is no transport left to select. No behaviour change.

### Fixed — a lifecycle event announced once, not once per gateway reconnect

- **`Initialize` is now idempotent, and the event coordinator is a singleton.** Discord's gateway
  READY fires again on every reconnect, and the READY handler re-ran the coordinator's
  initialization — which *appends* callbacks to the event handler's lists. So after one reconnect
  every start/stop announced twice, after two reconnects three times, for the life of the process.
  Observed live at 2× on this host. Guarded in both `ServerEventCoordinatorService.Initialize` and
  `KgsmServerEventHandler.Initialize`, and the coordinator's transient registration — the reason a
  guard alone could not have been enough — is now a singleton, matching how it is actually held.

### Changed — engine events come from the journal, not a socket

- **`KGSM__JournalDir` replaces `KGSM__SocketPath`.** The bot tails the engine's append-only event
  journal instead of binding a socket for the engine to dial out to. It no longer owns a path that
  another consumer could collide with, and the engine no longer needs to be told this consumer
  exists — a journal is a file any number of readers share. The four announcement handlers are
  unchanged.

  **The bot reads from the tail and keeps no position between runs**, deliberately. It *announces*
  to Discord channels, and an announcement is only meaningful while it is current: replaying a
  backlog on restart would post "server started" for a server that started and stopped hours ago.
  Missing what happened during a restart is the right trade — the durable record is kgsm-monitor's,
  and this surface was never it.

### Added — the Control Panel can configure the bot
- **`deploy/kgsm-bot.leaf.json` declares every setting the bot binds** — all 27, across Discord
  identity and channel behaviour, the KGSM connection, the inventory cache, the model and the agent
  loop, each with its type, coded default, bounds, unit and risk. `deploy.sh` installs it into
  `/var/lib/kgsm/leaves/`, where kgsm-api scans for it and renders the bot's configuration page.
  This makes the bot configurable from the Control Panel for the first time; editing also needs
  kgsm-api's `deploy/setup-leaf-config.sh` re-run to wire the restart channel, and until then the
  panel shows the values read-only and says why.
- **A coverage test fails the build if the descriptor and the code disagree.** It walks the options
  types the bot actually binds, in both directions. It also caught that the two per-name maps
  (`KGSM:Blueprints`, `KGSM:Instances`) cannot be delivered as one environment variable, so they are
  named as a pinned exclusion rather than promised on the panel.
- The Discord token is write-only; the token, both ids and all three socket paths are marked
  `wiring`. Deleting a server's channel along with the server, and the conversation database path,
  are `destructive` — each loses history that is not recoverable from elsewhere.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent): chowns `/opt/kgsm-bot`
  to the deploying user, seeds `/etc/kgsm-bot/kgsm-bot.env`, puts the real unit in
  `/etc/kgsm-bot/systemd/` with `/etc/systemd/system/kgsm-bot.service` symlinked to it, installs a
  polkit grant scoped to this project's units, enables the unit, and verifies the grant with the same
  unprivileged `systemctl` calls `deploy.sh` makes.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts**, and refuses up-front (before building)
  with "run `deploy/setup.sh`" when the host is not provisioned. It still never touches the live env
  file, so the Discord token survives a redeploy.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.

## [1.1.2] - 2026-07-28

### Added
- **Regression test pinning the bot's unknown-event tolerance** (`KgsmServerEventHandlerUnknownEventTests`):
  asserts `KgsmServerEventHandler.Initialize` registers exactly the four instance lifecycle handlers
  (installed/started/stopped/uninstalled) and zero `RegisterRawHandler` calls. kgsm-lib's
  `EventService.OnEventReceivedAsync` already takes the safe path on an unrecognised or unhandled
  event type — `_eventTypeMapping` miss logs "Unknown event type" and returns; a hit whose data type
  has no registered handler logs "No handler registered" and returns — so the bot never sees envelope
  types it didn't subscribe to. Phase 2 of `blueprint-editor-plan.md` added three `blueprint_*` event
  types, which the bot deliberately ignores; this test locks that invariant so a future raw-handler
  refactor in the bot would force a re-evaluation of unknown-event tolerance rather than silently
  inheriting it from kgsm-lib.

### Fixed
- **`ServerOperations` (the bot's `IServerOperations` adapter) now implements `GetConfiguredPortsAsync`
  and `WriteInstanceFileAsync`** as honest-degrade stubs returning a "not available on the Discord
  surface" failure — the same pattern as the existing `ReadInstanceFileAsync`/`ListInstanceDirectoryAsync`
  stubs. The two methods were added to the assistant's `IServerOperations` port (`kgsm-llm`) and the
  bot's adapter had not been resynced, which broke the build of `KGSM.Bot.Discord` (and by transitive
  reference the test project). No behavior change on Discord: both tools fall back to the failure path
  the bot already reported for the other assistant-only capabilities.

## [1.1.1] - 2026-07-02

### Changed
- Replace Discord.Net meta-package with individual sub-packages (drop unused Commands + Webhook assemblies).
- Remove unused FluentValidation dependency.

## [1.1.0] - 2026-06-30

### Added
- Initial versioned release.
