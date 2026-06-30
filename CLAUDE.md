# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-bot` is the **Discord surface onto KGSM** — one of the `kgsm-*` leaves in the
`tks` umbrella workspace. It is a **leaf consumer**: it reaches the engine only
through `kgsm-lib` (and the extracted assistant), never by shelling `kgsm.sh`
itself. Read the workspace root `../CLAUDE.md` + `../system-architecture.md` for the
ecosystem rules this repo inherits (the dependency spine, "never fabricate a
status", leaves-are-independently-deployable).

## Building requires the full umbrella checkout

This is the single biggest gotcha. The projects **ProjectReference their siblings by
relative path** — there is no NuGet fallback:

- `../../../kgsm-lib/kgsm-lib/kgsm-lib.csproj` (referenced by Core, Application, Infrastructure)
- `../../../kgsm-llm/TheKrystalShip.Llm/...` and `.../TheKrystalShip.Kgsm.Assistant/...` (the LLM agent loop + the extracted kgsm assistant)

So `kgsm-bot` only builds when `kgsm-lib/` and `kgsm-llm/` are checked out as siblings
under `tks/`. A standalone clone will not restore. (`nuget.config` adds a local feed
`/home/heisen/local-nuget` — legacy, only relevant if `TheKrystalShip.Llm` is ever
consumed as a package again; today it's a project reference.)

Targets **.NET 10** (the README still says 9 — trust the `.csproj`/the code).

## Commands

```bash
dotnet build kgsm-bot.sln
dotnet test  kgsm-bot.sln
dotnet test  kgsm-bot.sln --filter "FullyQualifiedName~ConfirmationIds"   # one class/test

cd src/KGSM.Bot.Discord && dotnet run        # run locally (needs appsettings.json — see below)

./deploy/deploy.sh                            # build + deploy to systemd on this host
```

`deploy/deploy.sh` publishes a **framework-dependent single-file** binary (the host
must have the .NET 10 runtime; it is not bundled), syncs it into `/opt/kgsm-bot`, and
manages the `kgsm-bot.service` unit. It builds **as the invoking user** and only
`sudo`s the systemd/root-path steps; it never overwrites the live env file (the
Discord token survives a redeploy). Tests/local-run ignore the publish-only props.

## Configuration

`src/KGSM.Bot.Discord/appsettings.json` is the live config (gitignored secrets);
copy `appsettings.example.json`. In production the **Discord token comes from the env
file** (`/etc/kgsm-bot/kgsm-bot.env` → `Discord__Token`), never the committed JSON.
Key sections: `Discord` (token, `GuildId`, `ActionRoleId`), `KGSM`
(`Path` to `kgsm.sh`, `SocketPath` for the bot's own event socket, `WatchdogSocketPath`),
`Ollama`/`Conversation`/`Llm` (the assistant), `KgsmCache` (inventory TTLs).

## Architecture

Clean Architecture, four projects + tests. The dependency direction is
`Discord → Infrastructure → Application → Core`; Core depends only on `kgsm-lib`
models + logging abstractions.

- **`KGSM.Bot.Core`** — interfaces (`IServerInstanceService`, `IKgsmStateCache`, …),
  the `Result`/`Result<T>` type, and `InvocationContext` (provenance, below).
- **`KGSM.Bot.Application`** — the **MediatR** layer: commands/queries (`ServerCommands.cs`,
  `ServerQueries.cs`), their handlers, and `ServerEventCoordinatorService` (wires kgsm
  lifecycle events → Discord notifications + cache invalidation).
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
   `/start`, `/stop`, `/list`, `/status`, `/supervision`, blueprints, etc.
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

A single `Discord.ActionRoleId` gates **all mutations**; read-only commands are open
to everyone. If no role is configured, no one can mutate. The check is enforced at
both entry points (`MessageHandler`, slash modules) **and re-checked at the Confirm
click** — don't rely on a single layer.

### Provenance (who did what, attributable to kgsm)

`Core/Common/InvocationContext.cs` is an `AsyncLocal`-backed ambient holder. Entry
points call `_invocation.Begin(Invocation.ForDiscordUser(username))`; the kgsm
chokepoint (`KgsmServerInstanceService`) reads `Current` and stamps
`actor`/`origin` (`discord:<user>` / `discord`) onto every mutating `kgsm-lib` call,
so the engine's audit events are attributable. Outside any scope `Current` is null and
kgsm applies its **honest OS-user fallback — never a fabricated identity**. When adding
a new mutating entry point, wrap it in a `Begin(...)` scope.

### State cache & events

`KgsmStateCache` caches the instance/blueprint inventory (TTL backstop +
event-driven invalidation) so the bot doesn't spawn a `kgsm` subprocess per message;
on a refresh failure it **serves the last-known-good snapshot rather than blanking**.
The bot binds its own kgsm event socket (`KGSM.SocketPath`); kgsm dials *out* to it,
so that path must be in kgsm's `event_socket_filenames`. Lifecycle events
(installed/started/stopped/uninstalled) drive both Discord notifications and cache
invalidation via `ServerEventCoordinatorService`.

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
