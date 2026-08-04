# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
