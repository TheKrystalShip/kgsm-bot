# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
