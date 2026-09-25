# Slash commands, buttons and the command manifest

Every module here is a Discord.Net `InteractionModuleBase`. Anything that mutates a server goes
`IMediator.Send` → handler → `IServerInstanceService` → `kgsm-lib`, the same path the @-mention
surface takes; a new mutating entry point opens an `_invocation.Begin(Invocation.ForDiscordUser(...))`
scope so the engine can attribute it.

## Gating

What an account answer means is `src/KGSM.Bot.Infrastructure/Authorization/CLAUDE.md`. Here:

- **Each module carries `[RequireTier(KgsmTier.Viewer)]`**, so a command from an account this host
  does not have is refused rather than answered. **`[Mutating]` is the operator gate**: it derives
  from `RequireTierAttribute`, so the attribute that puts a command in the panel's "acts" column is the
  same one that decides who may run it, and a new mutating command cannot be added and left open by
  forgetting a second attribute. `InteractionHandler` prints the precondition's reason **verbatim** —
  prefixing "you don't have permission" onto "your account isn't connected" states the one thing that
  is not true about it.
- **Confirm buttons re-resolve at the click** rather than trusting the staging turn — the account
  someone held when the button was posted is not necessarily the one they hold now. They authorize
  inline rather than by precondition, because a refusal must leave the prompt standing for whoever
  *is* permitted instead of failing the interaction.
- `CommandManifestTests` pins all of it: the manifest's `gate` must equal what the modules enforce,
  every mutating command must require operator, and every slash module must require an account.

## Destructive ops from the assistant: stage → confirm

The assistant never executes an action inside a turn — it **stages** one and returns an opaque grant,
which `MessageHandler` renders as Confirm/Cancel buttons (`AssistantConfirmationIds.cs`). The button
carries the grant and nothing else, so **the bot holds no part of the pending action**: a restart on
either side leaves a posted button working, and one lifetime governs it
(`Assistant:Confirmation:TtlSeconds`, the assistant's).

`AssistantConfirmationModule` forwards whoever clicked and the tier they hold at that moment. **The
assistant is the gate**: it re-derives authority, re-validates the target against live inventory,
refuses a grant that is already redeemed, and refuses one belonging to somebody else.

**Only the person who asked can approve.** A conversation belongs to one person and so do the actions
in it — the same rule the Control Panel follows. The bot's own operator check in front of the call is
a courtesy to the clicker, never the gate, and a refusal leaves the prompt standing for whoever is
permitted.

The Cancel id lives **outside** the confirm prefix: the confirm handler matches `kgsmact~*` on a
wildcard, and a cancel id underneath it would be captured and read as a grant.

## Restores (`/backups`, `/backup`, `/restore`)

A restore is staged (`IStagedRestores`, 32-hex handle). **Confirming is authorized at the click *and*
restricted to the person who proposed it.** A restart button is a shortcut to a command anyone with the
tier could type, so anyone with the tier may press it; this one names a specific archive somebody else
chose. The handle is **peeked before it is redeemed**, so a click that is not allowed leaves the
proposal standing for whoever is. Cancelling is open to anyone — the asymmetry is deliberate, one
direction destroys and the other does nothing.

## `/setup` owns the topology

- **`[RequireTier(KgsmTier.Admin)]`, never a Discord permission.** Gating `/setup` on *Manage Server*
  would let anyone who can add the bot to a guild of their own point this host's announcements —
  including player joins and leaves — into it, and authorizing correctly would not help: an
  announcement has no caller to authorize. **Two questions, two refusals**: the tier (*may you
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
  are filtered; slash commands are not. Authority in this ecosystem is the KGSM account, host-wide —
  filtering *reads* by which guild they were typed in would be a second, per-guild authority model,
  which is banned. A viewer is trusted with this host's inventory wherever they ask from.

## Names

Autocomplete entries and the `/setup` listings print a server's label and its id; the value
autocomplete hands back is always the id. **`/install`'s `name` is the display name** — the engine
mints the id; a person only ever chooses what the server is shown as.

## Read commands answer one person

`/status`, `/list`, `/is-active` and `/supervision` reply ephemerally under `EphemeralReads` (on) — a
busy channel does not need everyone's status checks in its scrollback. `/history` follows the same
switch. **`/connect` is deliberately not one of them**: its whole purpose is to be read by somebody
other than the person who typed it. `/logs` and `/health` are operator-gated and always private
whatever the switch says — a game log carries player IP addresses, and a failing health check names
host paths and the reasons stores could not be opened.

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
- **The bucket is the tier the command is actually refused below** — its own `RequireTier`, else its
  module's, taking the higher of the two because Discord.Net evaluates both. So the panel prints the
  word the precondition enforces rather than one derived from it, and `/setup` lands in `admin` on the
  strength of changing no server at all.
- **`[Mutating]` is the one thing reflection cannot see.** It marks a command that changes something,
  which is what splits the panel into what reads and what acts. `CommandManifestTests` pins the set by
  name, so a new acting command that is not marked fails the build rather than being listed to an
  operator as read-only.
- **The operator bucket is not only the acting commands.** A command can need operator without
  changing anything — `/logs` and `/health` both show the inside of the machine — so the test asserts
  the direction that protects (everything marked `[Mutating]` is gated at operator) and names the reads
  that sit there, rather than asserting the reverse and forcing a read to be mislabelled as an action.
- **The tests compare against Discord.Net itself** — the same `InteractionService.AddModulesAsync`
  scan the bot runs at startup, command for command and option for option. The manifest is read by a
  process that never talks to Discord, so agreeing with what is actually registered is the only thing
  keeping it true.
