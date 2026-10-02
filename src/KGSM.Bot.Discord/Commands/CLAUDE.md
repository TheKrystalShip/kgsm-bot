# Slash commands, buttons and the command manifest

Every module here is a Discord.Net `InteractionModuleBase`. Anything that mutates a server goes
`IMediator.Send` → handler → `IServerInstanceService` → `kgsm-lib`, the same path the @-mention
surface takes; a new mutating entry point opens an `_invocation.Begin(Invocation.ForDiscordUser(...))`
scope so the engine can attribute it.

## Gating

What an account answer means is `src/KGSM.Bot.Infrastructure/Authorization/CLAUDE.md`. Here:

- **Every slash command carries `[RequireAction(action)]`**, naming the action it performs — the same
  action the Control Panel and the assistant check for the same thing (`kgsm:server.start` for
  `/start`, `kgsm:server.console.read` for `/logs`), or one of the bot's own (`bot:status.read`,
  `bot:announcements.manage`, `bot:voice.use`). `Server = "instance"` names the option the server is
  given in, and the person is judged **at that install**; with the option left out, or
  `AnyServer = true`, the command is over the whole host, admits somebody holding the action anywhere
  on this node, and shows them only the servers they hold it at. Without `Server` the command is
  judged at this node. `InteractionHandler` prints the precondition's reason **verbatim** — prefixing
  "you don't have permission" onto "your account isn't connected" states the one thing that is not
  true about it.
- **`[Mutating]` decides nothing.** It marks a command that changes something, for the panel's "acts"
  column; who may run it is its action.
- **Buttons re-evaluate at the click** rather than trusting the staging turn — what someone held when
  the button was posted is not necessarily what they hold now. The restart button checks
  `kgsm:server.restart` and the restore button `kgsm:server.backups.restore`, at the server they name.
  They authorize inline rather than by precondition, because a refusal must leave the prompt standing
  for whoever *is* permitted instead of failing the interaction.
- **Autocomplete shows what the person can see**: server suggestions are cut to the servers they can
  read, and blueprints and libraries are offered to whoever holds `kgsm:library.read` and
  `kgsm:engine.config.read` here.
- `CommandManifestTests` pins all of it: the manifest lists exactly what Discord registers (a command
  with no action is not listed, so that is also the proof every command checks one), each command's
  action is named, every `Server` option is a real parameter, and no acting command checks only a read.

## Destructive ops from the assistant: stage → confirm

The assistant never executes an action inside a turn — it **stages** one and returns an opaque grant,
which `MessageHandler` renders as Confirm/Cancel buttons (`AssistantConfirmationIds.cs`). The button
carries the grant and nothing else, so **the bot holds no part of the pending action**: a restart on
either side leaves a posted button working, and one lifetime governs it
(`Assistant:Confirmation:TtlSeconds`, the assistant's).

`AssistantConfirmationModule` forwards whoever clicked, by handle. **The assistant is the gate**: it
evaluates the clicker for the staged command's action at its server, re-validates the target against
live inventory, refuses a grant that is already redeemed, and refuses one belonging to somebody else.

**Only the person who asked can approve.** A conversation belongs to one person and so do the actions
in it — the same rule the Control Panel follows. The bot's own `assistant:chat` check in front of the
call is a courtesy to the clicker, never the gate, and a refusal leaves the prompt standing for whoever
is permitted.

The Cancel id lives **outside** the confirm prefix: the confirm handler matches `kgsmact~*` on a
wildcard, and a cancel id underneath it would be captured and read as a grant.

## Restores (`/backups`, `/backup`, `/restore`)

A restore is staged (`IStagedRestores`, 32-hex handle). **Confirming is authorized at the click *and*
restricted to the person who proposed it.** A restart button is a shortcut to a command anyone holding
the restart could type, so anyone holding it may press it; this one names a specific archive somebody
else chose. The handle is **peeked before it is redeemed**, so a click that is not allowed leaves the
proposal standing for whoever is. Cancelling is open to anyone — the asymmetry is deliberate, one
direction destroys and the other does nothing.

## `/setup` owns the topology

- **`bot:announcements.manage`, never a Discord permission.** Gating `/setup` on *Manage Server*
  would let anyone who can add the bot to a guild of their own point this host's announcements —
  including player joins and leaves — into it, and authorizing correctly would not help: an
  announcement has no caller to authorize. **Two questions, two refusals**: the action (*may you
  configure this host*) and the bot's own Discord permission, checked **before** anything is recorded
  (*can I actually do it here*) — recording a channel it cannot post in is how a guild gets configured
  and then silently receives nothing. Not `[Mutating]`: it changes no server.
- **`/setup announce <channel>` alone is a working configuration.** The board — a channel per server,
  under a category — is opt-in per guild because it needs `Manage Channels`, which is the permission
  people reasonably think twice about granting. `/setup board-off` and `/setup forget` **never delete
  a channel**: deleting one with history because a setting changed is not a decision a bot gets to
  make, and the reply says so.
- **Each guild follows the servers it chooses, and `empty means all`.** No rows is no filter — a guild
  that wants silence runs `/setup forget` instead. `/setup follow` narrows (the *first* one narrows to
  that server alone, and the reply says so), `/setup unfollow` widens, `/setup follow-all` clears it.
  **Unfollowing the last one is refused**: emptying the list means *everything*, which is the opposite
  of what somebody removing their last server is asking for, so both real choices are named instead.
- **The filter governs what the bot says unprompted, never what it answers when asked.**
  Announcements, the per-server channel an install creates, and the rows on the live status message
  are filtered by it; slash commands are not. Authority in this ecosystem is the KGSM account's grants
  — filtering *reads* by which guild they were typed in would be a second, per-guild authority model,
  which is banned. A person sees the servers they can read wherever they ask from.

## Names

Autocomplete entries and the `/setup` listings print a server's label and its id; the value
autocomplete hands back is always the id. **`/install`'s `name` is the display name** — the engine
mints the id; a person only ever chooses what the server is shown as.

## Read commands answer one person

`/status`, `/list`, `/is-active` and `/supervision` reply ephemerally under `EphemeralReads` (on) — a
busy channel does not need everyone's status checks in its scrollback. `/history` follows the same
switch. **`/connect` is deliberately not one of them**: its whole purpose is to be read by somebody
other than the person who typed it. `/logs` (`kgsm:server.console.read`) and `/health`
(`bot:status.read`) are always private whatever the switch says — a game log carries player IP
addresses, and a failing health check names host paths and the reasons stores could not be opened.

**`/logs` sends the tail as a file.** Ephemeral is a privacy decision, not a tidiness one: this bot
already refuses to put an address in a roster, and posting the raw log into the channel would publish
the same thing with more of it. A file rather than a code block because Discord truncates a long one
and wraps every line of it on a phone. Oversized logs are trimmed **from the front** — the end is the
part somebody asked for — and the budget is counted in bytes, since that is what Discord's limit is in.

## The command manifest: the Control Panel's list comes from the binary

`deploy/kgsm-bot.commands.json` is the catalog the Control Panel shows on this leaf's **Commands** tab.
It is **generated, not written**: an `AfterTargets="Build"` target runs the binary it just produced
with `--emit-commands`, and `CommandManifest.cs` reflects over this assembly's own
`InteractionModuleBase` types. So the file cannot name a command that does not exist, and a rename or
a new option reaches the panel with no second edit. Commit what the build produces.

- **A shipped file, not an endpoint**, for the same reason the config descriptor is one: the list is
  wanted when the unit is stopped. `deploy.sh` installs it into
  `/var/lib/kgsm/leaves/commands/bot.json` — a subdirectory, because the descriptor scan globs `*.json`
  at the level above and would read it as a malformed descriptor. Format:
  `leaf-command-manifest.md` at the workspace root.
- **Each command names the action its `RequireAction` checks** (schema 3), so the panel prints the
  action that decides the answer rather than one derived from it. A command with none is not in the
  catalog.
- **`[Mutating]` is the one thing reflection cannot see.** It marks a command that changes something,
  which is what splits the panel into what reads and what acts. `CommandManifestTests` pins the set by
  name, so a new acting command that is not marked fails the build rather than being listed as
  read-only. A command can be gated on an action of its own without changing anything — the voice
  commands change no server, and the bot in a voice channel hears everybody in it.
- **The tests compare against Discord.Net itself** — the same `InteractionService.AddModulesAsync`
  scan the bot runs at startup, command for command and option for option. The manifest is read by a
  process that never talks to Discord, so agreeing with what is actually registered is the only thing
  keeping it true.
