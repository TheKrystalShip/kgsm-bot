# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed — the bot hears every producer, so incidents reach Discord again

**Six of the seventeen events this bot announces are not the engine's to emit.** The supervisor owns
`instance_crashed`, `instance_failed`, `instance_started`, `instance_ready`, `instance_restarted` and
player presence, and it writes them to its own journal. Reading only the engine's, this bot announced
installs, backups, updates and stops perfectly while saying nothing about a single crash, give-up or
player joining — and the incident thread and the restart button, which hang off those announcements,
could never fire. From inside a channel that is indistinguishable from a host where nothing went wrong.

`AddKgsmJournalFederation` now runs after `AddKgsmServices`, so both the live tail and `/history` read
every producer's journal: the engine's, the supervisor's, the firewall authority's and the monitor's.
Nothing else moved — every handler is registered against `IEventSource` and never learns what backs it.

⚠ **The call must stay after `AddKgsmServices`.** Above it, the single-journal registration wins,
nothing throws, nothing is logged, and the bot goes quiet about incidents again.
`JournalFederationWiringTests` resolves both halves out of the container the bot actually builds, since
that is a failure with no symptom to notice.

Start position and cursor are unchanged (tail, none): the federated source keeps one position per
producer, so a cursor would replay each journal's backlog independently after a restart and announce a
morning's crashes at once.

### Changed

- **kgsm-lib 4.23.1**, from 4.9.0. Carries journal federation and the per-producer event ids.

### Added

- **The assistant investigates a server the supervisor gave up on, before anybody asks.** The thread
  under a give-up opened empty — at the one moment when everything needed to explain the failure had
  already been gathered by the host and nobody had read any of it. It now opens with the assistant's
  findings: the console of the run that died, a health check, recent events, and the operator guides
  for the game it runs.
  - **Give-ups only.** A crash mid-streak is already being restarted, and one report per attempt
    during a crash loop is a thread full of writing about a problem that is still happening.
  - Posted as the thread's opening turn, so whoever asks next continues *that* conversation with the
    findings in context rather than starting cold beside them.
  - **It reads; it does not act.** The turn is asked with auto-run off, and anything the model
    proposes is dropped rather than offered to a thread that asked for nothing — the announcement
    above it already carries the one action that belongs there, which is a restart.
  - It runs at operator because the console of the run that died is an authorized read and is the one
    artifact that explains a crash; at viewer it would report on a server whose logs it could not open.
  - One investigation at a time host-wide, and the same server is left alone for 15 minutes in the
    same guild afterwards. A host that runs out of memory takes every server down at once, and those
    give-ups would otherwise arrive at one Ollama together.
  - Off with `Discord:IncidentTriage`, and inert with no assistant configured or no thread to post in
    — a give-up is then announced exactly as it was.
  - A failed investigation says so in the thread. Somebody is looking at a server that is down, and
    silence is indistinguishable from an investigation that found nothing wrong.
  - **The thread shows what it is consulting, as it consults it** — one message posted when the
    investigation starts and edited as each step lands, then finished with the findings in the same
    message. The same account of a turn the Control Panel's chat shows, in the place people are
    reading. **It describes, never quotes:** a step says what was looked at and what the result was
    about, never a tool's own output — a console read returns the server's log, which carries the
    network address of everyone who connected, and this bot already keeps `/logs` out of the channel
    for exactly that reason.
  - The same narration runs on the @-mention surface, so a question asked in a channel shows the work
    instead of a silence of unknown length.
  - The thread shows the bot **typing** while it works — the same indicator, from the same call, that
    somebody gets when they @-mention it. It starts once the investigation actually begins rather than
    while it waits for the slot, and stops whether the turn produced findings or failed.

- **A thread is one conversation, shared by everyone in it.** Talking to the assistant in a thread was
  N private conversations that happened to be in the same place: each person's questions landed in
  their own memory, so the answer to the fourth question was written as though the first three had
  never been asked. A thread now asks as a *room* — one transcript everyone continues, which is what
  people talking in a thread already believe is happening.
  - Threads only. A channel is a room nobody joined and nobody leaves, where an exchange between two
    other people an hour ago is context this conversation never had.
  - **Shared transcript, unshared authority.** Each person's tier still travels with their own
    question, so what the assistant will do for them is what *they* may do — a Viewer asking in a room
    an Operator is also in gets a Viewer's answer.
  - A staged action is still confirmed by whoever staged it: the assistant refuses a grant that is not
    the clicker's, in a room exactly as anywhere else.
  - The per-channel scope still travels beside the room, so an assistant that predates rooms reads
    that instead and gives each person their own context window — a worse conversation, not a broken
    one. Needs `TheKrystalShip.Kgsm.Assistant.Relay` 1.1.0.

### Changed

- **`/var/lib/kgsm-bot` is provisioned by systemd, not by `setup.sh` under sudo.** The unit declares
  `StateDirectory=kgsm-bot` with `StateDirectoryMode=0750`, so systemd creates the directory owned by
  `User=` before `ExecStart` and exports `$STATE_DIRECTORY`, which `SqliteGuildStore` opens the guild
  store from. `Guilds:DbPath` set to anything other than its shipped default still wins, and the
  shipped path remains the fallback for a bot run outside systemd, so `guilds import` from a terminal
  reads the same file. The path is unchanged and `bot.db` is untouched; the directory is now `0750`,
  and provisioning it costs no privilege, works under any `User=` the deploy templates in, and needs
  no home directory.

- **`/history` reads the engine's own classification of its events instead of keeping its own.**
  kgsm-lib 4.8.0 ships `KgsmEventCatalog` — what each event is about, whether it is the news or a step
  inside it, and what kind of data each payload field holds — and the bot now asks it rather than
  answering both questions locally. Two consequences on the surface:

  **What may be printed is the engine's word.** The reader prints a payload field only where the
  catalog calls it public and scalar, so a player's network address, console input verbatim, and
  structured ports stay off a line for the reason each is what it is rather than because this repo
  remembered to exclude them. A moderation target joins them: the event does not say whether it is a
  name or an address — only the game's blueprint does — so a surface that cannot resolve it treats it
  as personal. A field reclassified upstream changes what Discord shows with no edit here.

  **The steps inside an operation are no longer listed beside it.** An install brackets its work with
  a dozen events around the one that is the news; over two real days here that is 17% of the journal
  spent on scaffolding. An unrecognised type counts as news, so a type the engine starts emitting
  tomorrow still appears, and a failure is news whatever step it happened inside. The footer says how
  many steps were left out — a filtered list presented as the whole window is the one thing this must
  not look like.

### Added

- **`/history [server] [hours]` — what happened, read back out of the engine's journal.** Viewer-gated,
  one server or the whole host, default 24 hours and up to 30 days. The durable record answers it, so
  a window reaching back over a restart is exactly what it is for.

  **Every way of failing to answer is rendered as itself.** An unreadable journal says so instead of
  reporting a quiet host — the two look identical if you only count events. A window reaching further
  back than the journal keeps is answered from where the record starts and says which date that is. A
  scan that stopped at its budget says the page is a prefix. The one empty answer that means "nothing
  happened" is the one with a readable journal behind it.

  **The engine's vocabulary is bigger than the bot's, and an unrecognised type is never dropped.** A
  measured day on this host carries deploy phases, UPnP forwards, port openings and prune results
  that no announcement kind exists for. The types worth naming are named; everything else renders
  from the engine's own word with its prefix stripped, which is also what a type added upstream
  tomorrow gets with no change here.

  One field is lifted verbatim off each payload — which setting changed, which player, which version —
  and which fields may be printed is the engine's own classification rather than a list kept here.

- **`/health` — whether everything this bot depends on is answering.** Operator-gated and always
  private, like `/logs`: a failing check names host paths and the reasons stores could not be opened.

  The state it exists for is the one nothing else shows — the unit is active, the gateway says
  Connected, and the bot cannot do the thing somebody just asked it about. Seven checks, each run at
  the moment it is reported: the gateway, the outbound queue, the engine (asked by actually asking
  it, not by reading the inventory cache), the event journal, the KGSM account store, the guild
  store, and the assistant.

  **No check is inferred from another** — they fail independently in practice, and a summary that
  took one as evidence for the next would report a state that was never measured. **Four verdicts,
  not two:** a dependency this host was never given is not broken, and a check that could not reach
  an answer is not a pass. An undeployed assistant is left out of the count entirely rather than
  making a correct host read as permanently short of something.

- **Backups reach Discord: `/backups`, `/backup`, `/restore`.** The biggest thing this host measured
  and never surfaced.

  **How consistent each capture was is the part worth reading, and the engine measures it per
  backup:** `cold` (the server was stopped, so nothing could write mid-archive), `flushed` (running,
  but it wrote its world out first), `hot` (running with no usable save command — **the archive may
  be torn**), or nothing at all when the run state could not be read. Each is spelled out rather than
  flattened into a tick, and an unrecognised value is printed as it came — the engine owns that
  vocabulary, and guessing what a new word means is how a torn archive gets called a good one.

  **`/restore` is staged behind a confirmation button**, authorized at the click and restricted to
  the person who proposed it — a restart button is a shortcut to a command anyone could type, but
  this one names a specific archive somebody else chose. The button carries a 32-hex handle, not the
  operation: a server name and a backup id together do not reliably fit a 100-character `customId`,
  and a truncated one would name a *different* archive rather than failing. Proposals live in memory
  for five minutes, because a destructive action that survives a restart is one somebody clicks by
  accident days later. Redeeming is one-shot.

  **The live status message flags a backup only when it is worth flagging** — older than
  `Discord:BackupStaleAfterHours` (48h), or never taken. An age beside all sixteen servers buries the
  one that matters among fifteen that do not. It is cached per server and dropped on the engine's own
  backup events; what is cached is the *timestamp*, so the age is always computed fresh and a stale
  cache still reads correctly.

  Throughout, "read and has no backups" and "could not be asked" stay different answers.

- **Read commands answer the person who asked.** `/status`, `/list`, `/is-active` and `/supervision`
  reply ephemerally under `Discord:EphemeralReads` (on by default), so a busy channel keeps its
  scrollback. `/connect` is deliberately excluded — its whole purpose is to be read by somebody other
  than the person who typed it.

### Fixed

- **A server whose Discord channel was deleted lost every announcement about it.** The per-server
  channel binding was treated as the only place that server could report, so once somebody deleted
  the channel the bot resolved to it, failed, and said nothing — silently, indefinitely. Seven of the
  sixteen bindings on this host were in that state.

  Announcements now **fall back to the guild's announcement channel**, which is the one channel every
  guild is required to have and which everything else already falls back to. A binding is a
  preference; it is not permission to go quiet.

### Added

- **Stale channel bindings are reconciled once, on connect.** `ReconcileBindingsAsync` checks every
  binding whose channel is missing from the gateway cache and forgets the ones Discord confirms are
  deleted, so `/setup show` stops listing channels that do not exist and a reinstall does not try to
  reuse one.

  ⚠ **"Not visible" is not "deleted", and the difference decides whether a binding is destroyed.** A
  channel the bot has lost `View Channel` on is missing from the cache exactly like one that no longer
  exists, and unbinding it would orphan a live channel full of a server's history with nothing
  pointing at it. So a cache miss is confirmed against Discord — which answers nothing for a deleted
  channel and refuses for one it will not show — and anything other than a clean "no such channel"
  leaves the binding where it is. A guild the bot cannot see at all is skipped, so a gateway outage
  drops nothing.

  Once per process, on the cache misses only, and through the send queue like every other unprompted
  call. A channel deleted mid-session is already survived by the fallback; this tidies the record
  afterwards.

- **Each Discord server follows only the game servers it chooses.** A guild running one game was
  hearing about all sixteen on this host, which is the real multi-tenancy gap now that more than one
  guild is possible. `/setup follow <server>` narrows, `/setup unfollow <server>` widens,
  `/setup follow-all` clears it, and `/setup show` lists what a guild follows.

  **Empty means all**, and that is the load-bearing decision: no rows is no filter, which is exactly
  what every guild configured before this existed already has — so a host upgrading keeps hearing
  what it heard yesterday rather than going silent for a reason nobody in Discord can see. It also
  means **unfollowing the last server is refused**: emptying the list would turn a guild that hears
  about one game into a guild that hears about all of them. The refusal names both real choices
  (`/setup follow-all`, or `/setup forget` for silence).

  The filter reaches everything the bot says **unprompted** — announcements, the per-server channel
  an install would create, and the rows on the live status message, which is read once for the host
  and narrowed per guild so a board cannot contradict the filter sitting beside it. It deliberately
  does **not** reach slash commands: authority here is the KGSM account and it is host-wide, so
  filtering reads by which guild they were typed in would be a second per-guild authority model —
  the thing `KgsmRoleMap` was, and which is banned ecosystem-wide.

  Two failure modes were closed on the way. An **unreadable** filter follows everything, so a store
  that cannot be read is loud in the log rather than quietly muting a guild. And a server that is
  **uninstalled is not unfollowed** — the stale row is correct in every case, where dropping it would
  empty a one-server list and silently switch that guild to following all of them.

  Guild store schema 3: a `guild_servers` table, additive, and `/setup forget` takes a guild's filter
  with it.

- **The bot says something when it is added to a Discord server.** A guild with no row hears nothing
  by design, and from inside Discord that is indistinguishable from a bot that is broken. It now
  posts one introduction on joining — what it does, that the silence is deliberate, that
  `/setup announce` alone is a working setup, and that running it needs KGSM admin rather than a
  Discord role.

  Said once, never repeated, and it grants nothing. It looks for the system channel, then the first
  channel it can actually post in, then the owner's DM, **checking each rather than attempting it**,
  so finding nowhere to speak costs no requests. A guild that is already set up is not greeted: that
  is a reconnection, not an introduction.

- **The bot's own Discord presence — `Watching 6 servers · 3 online · 12 playing`.** The one thing
  this bot says with no channel to say it in: it reaches a Discord server that has never run
  `/setup`, one update covers every one of them at once, and it is visible in the member list without
  opening anything.

  A gateway presence update is limited to a handful per twenty seconds for the whole session, and
  that budget is not the one `IDiscordSendQueue` paces, so `Discord:PresenceRefreshSeconds` (60s,
  floor 20) is the only thing protecting it: the line is recomposed on a **fixed tick, never in
  response to an event**, and sent only when it actually changed. `Discord:Presence` turns it off.

  It claims nothing it did not read. A host that could not be read says so rather than showing the
  last good numbers, an incomplete count is written as a floor (`3+ online`, `12+ playing`), and
  "0 playing" is never said — noise on a quiet host, and a lie on one whose games report nobody.

- **`/logs <server> [lines]` — the tail of a server's log, as a file.** Operator-gated, 200 lines by
  default. Diagnosing anything used to mean leaving Discord.

  **The reply is ephemeral, and that is a privacy decision rather than a tidiness one.** A game
  server's log routinely carries the network address of every player who connected; this bot already
  refuses to put an address in a roster for that reason, and posting the raw log into the channel
  would publish the same thing with more of it. A file rather than a code block because Discord
  truncates a long one and wraps every line of it on a phone. A log too big to upload is trimmed
  **from the front** — the end is the part somebody asked for — and the budget is counted in bytes,
  which is what Discord's limit is in.

- **`/players [server]` — who is playing, on one server or across the host.** Viewer-gated. The
  single most-asked question in a game Discord after "how do I join", and the bot could not answer it.

  The roster comes from the supervisor's live session map through `IPlayerRoster` (kgsm-lib 4.6.0),
  which joins three facts that have to agree before a number means anything: whether the server is
  running (the engine), whether its players can be observed at all (the supervisor), and who is
  connected. **Every way of not knowing is said out loud, and none of them is "0 online":**

  | | what it says |
  |---|---|
  | running, observed | the names, or "nobody is connected right now" — a measured zero |
  | stopped | "stopped, so nobody is on it" |
  | game reports no players | "this game doesn't report its players" — the server may be full |
  | supervisor unreachable | "I couldn't ask the supervisor" |

  A host summary sums only what was actually counted, and says how many servers it could not speak
  for rather than leaving them out of a total that would then read as complete. A player the game
  named is listed; **an unnamed session is counted but not labelled — the network address is
  deliberately not a fallback**, because it identifies a connection rather than a person and would
  publish a player's IP to the channel.

  Presence is detected from log output for some games and an RCON poll for others; which applies is
  the supervisor's business, and this surface never asks a game directly.

- **The live status message carries player counts**, from that same service — there is exactly one
  place a count comes from, so the board and the command cannot print different numbers about the
  same moment. A count appears only where it was measured and only when somebody is on; a server
  whose players cannot be seen gets no number rather than a zero, and the host total is written as a
  floor (`12+ playing`) whenever any server could not be counted.

- **One paced queue in front of everything the bot says unprompted.** Announcements fanning out
  across guilds, the status board's per-guild edits, channels created and retired with an install,
  and expiring messages cleaned up were four producers each able to burst, none aware of the others.
  Rate-limit headroom is a host-wide resource and being throttled off the API loses everything else
  with whichever call spent the last of it — the same failure that makes run state in a channel name
  unbuildable. All of it now goes through `IDiscordSendQueue`: one worker, a floor between calls
  (`Discord:SendQueueMinIntervalMs`, 200ms), a bounded backlog per lane
  (`Discord:SendQueueCapacity`, 500) and a doubling hold-off after a rate limit or a server error
  (`Discord:SendQueueBackoffMs` → `Discord:SendQueueMaxBackoffMs`, up to
  `Discord:SendQueueMaxAttempts` tries).

  The floor is the part that matters — a 429 has already spent the request that earned it. The
  hold-off pauses the whole queue rather than spinning one call against a limit while the rest starve
  behind it. Only a rate limit, a server error or a dropped connection is re-tried; a refusal, a
  missing channel or a malformed request is the answer and fails on the first attempt. A full lane
  **refuses and says so**, so a caller's own accounting shows the guild it did not reach — a silent
  drop would make a bot announcing nothing look like a host where nothing happened. Two lanes:
  announcements drain ahead of housekeeping, because a crash notice delayed behind fifteen board
  refreshes is the news arriving after the incident, while a board refresh that lands a moment later
  says the same thing.

  **Slash-command replies deliberately do not go through it.** Discord gives three seconds to
  acknowledge an interaction, and a reply queued behind a backlog arrives after the token is dead.

  The status socket carries the backlog (`sendQueue`: waiting per lane, and whether it is holding
  off). Connected, configured, every channel visible and messages arriving minutes late is a real
  state, and a depth that does not come back down is its only symptom.

- **"Update available" announcements** (`Discord:Announce:UpdateAvailable`, on by default). A channel
  hears when a newer game build is released for one of its servers — `🆕 **factorio** has an update
  available — 1.4.1 → 1.4.2` — which is the cue to run `/update`.

  The bot subscribes to `instance_update_available` and does nothing else: the engine decides what is
  worth announcing, because it records what each check found and emits only for a version it has not
  reported before. So a channel sees one message per new build however often this host checks, and
  how often it checks is the scheduler's business. Nothing here polls, compares versions or remembers
  an answer — the catalog rule that a kind exists only if the journal carries it is unchanged; the
  journal now carries this one.

- **A live status message, one per Discord server** — `/setup status <channel>` keeps one pinned
  message showing every server on this host, whether it is up, and how to reach it; `/setup
  status-off` stops updating it and leaves it standing. This is the ambient status board the channel
  markers were meant to be: Discord's channel-edit rate limit makes a name-based one unbuildable, and
  a message edit is a different bucket entirely. An event marks the picture dirty and
  `Discord:StatusMessageMinIntervalSeconds` (15s) decides when to spend an edit, so a host reboot's
  one-event-per-server burst costs a single edit; `Discord:StatusMessageRefreshSeconds` (900s) is a
  backstop for what no event describes. The message id is stored, so a restart edits the board that
  is already there instead of posting a second one and keeping the wrong one current.
- **`/connect <server>`** — the address, the ports, and whether those ports are actually reachable.
  Ports come from the engine's canonical `[{start,end,protocol}]`; the address is
  `Discord:PublicAddress` when the operator set one (a host cannot discover its own DNS name), else
  the external IP the host measured, stated as the changeable thing it is. Reachability comes from
  the kgsm-firewall authority over `KGSM:FirewallSocketPath`, read-only and optional: an inactive
  backend is reported as *unfiltered* rather than closed, an authority that cannot enumerate is
  *unknown*, and an absent one costs that one line.
- **Announcements you can act from.** A server the supervisor gave up on carries a **Restart**
  button — a shortcut to `/restart`, re-resolved against the account store at the click and stamped
  with the clicker's provenance. A crash opens a **thread**, so the conversation about an incident
  stays with it and the assistant can be asked there in its own context. `Discord:ActionButtons` and
  `Discord:IncidentThreads` switch each off; a missing permission costs the thread and nothing else.
  The button is deliberately not offered on `instance_crashed`, where the supervisor is already
  restarting the server.
- **Guild store schema 2** — `status_channel_id` / `status_message_id`, added in place. A version-1
  file is migrated, never recreated: losing it loses every channel binding.
- **`/setup` — the bot works in any Discord server, and each one is configured from inside Discord.**
  `/setup announce <channel>` is the whole of a working setup; `/setup board <category>` additionally
  gives each game server its own channel; `/setup board-off`, `/setup forget` and `/setup show`
  complete the surface. Gated at **KGSM admin** — deciding where a host broadcasts is a host setting,
  and gating it on Discord's *Manage Server* would let anyone who can invite the bot redirect this
  host's announcements into a server of their own. The bot's own Discord permission is a separate
  answer and is checked **before** anything is recorded, so a guild cannot be configured into
  silence.
- **A guild store**, SQLite at `/var/lib/kgsm-bot/bot.db` (`Guilds__DbPath`, mode `0600`), holding
  which Discord servers this host announces into and the channel each game server reports in.
  Deliberately outside `/opt/kgsm-bot`, which the deploy syncs with `rsync --delete`. Additive-only,
  with a schema-version floor check that refuses a file a newer build wrote.
- **`kgsm-bot --adopt-guild-config [--apply]`** moves a host configured for one Discord server into
  the store. Dry-run by default; `--from <settings.json>` names the file holding the old map and
  `--announce-channel <id>` supplies the guild's channel when the old configuration left it at zero.
  It refuses a guild that already has a row rather than merging.

### Changed

- **`ServerRoster` carries the run state it was decided against** (`Running`, null where the engine
  could not be asked). Working out what a roster means already requires asking the engine whether the
  server is up, at the cost of a kgsm process per server, and the live status message wants that fact
  too — it was spawning a second process per server per publish to ask the same question, and could
  have got a different answer about the same moment. The board now joins to the roster for both.

### Fixed

- **The "Game update available" switch appears in the Control Panel.** The kind was announced and the
  toggle was bound, but the status line the panel renders its switches from had no row for it — so the
  control was simply absent and an operator would conclude the bot could not be told to stop. The set
  is now pinned by a test against `AnnouncementOptions` itself, one row per declared toggle, because a
  missing row looks like nothing at all rather than like a failure.

- **A status board that could not be read is left alone instead of replaced.** A failed fetch of the
  recorded message was indistinguishable from the message being gone, so a refused request posted a
  second board beside the first — two of them disagreeing, one kept current. A fetch that fails is now
  reported and skipped; only a successful "no such message" posts a new one.

- **A channel the bot created on install is no longer forgotten at the next restart.** The binding
  was written to the in-memory options dictionary and nothing ever serialised it back, so after a
  restart the server fell back to the announcement channel and its channel was orphaned. Bindings
  live in the store.
- **Announcing no longer stops at the first guild that fails.** Each guild is resolved and sent to
  on its own; a failure is logged and the rest proceed, and the result counts guilds reached against
  guilds configured rather than reporting a bare success.

### Removed

- **`Discord__GuildId`, `Discord__InstancesCategoryId`, `Discord__AnnouncementChannelId` and the
  `KGSM:Instances` channel map.** ⚠ **A host upgrading past this must run
  `deploy/deploy.sh`'s new binary with `--adopt-guild-config --apply` *before* redeploying**, while
  the old settings file is still in place — otherwise the 15-odd channel bindings it holds are gone
  and every server is given a fresh channel beside the one carrying its history. Back up
  `/opt/kgsm-bot/kgsm-bot.settings.json` first.
- `InstanceSettings.Blueprint`, which was written and never read, and
  `IServerInstanceService.GetChannelIdAsync`, which asked the kgsm chokepoint a question only
  Discord topology could answer. `/list` reads the channel for the guild it was typed in.

### Changed

- **The status socket reports a row per configured Discord server** — resolution, the announcement
  channel's reachability, whether the board's permission is still held, and that guild's channel
  bindings — replacing the single `guildConfigured`/`guildResolved`/`channels` triple. It also
  carries whether the guild store could be opened at all, which is the one condition under which
  nothing is announced anywhere however healthy everything else reads.
- **The command manifest buckets each command by the tier its `RequireTier` actually demands** (the
  method's, else its module's) rather than inferring `operator`-or-`none` from `[Mutating]`. Read
  commands move from `none` to `viewer`, which is what the modules have always enforced, and
  `/setup` lands in `admin` despite changing no server.
- The manifest's option-type vocabulary covers Discord's entity options (`channel`, `role`, `user`,
  `mentionable`, `attachment`), so `/setup`'s channel and category options are listed as what
  Discord will ask for.

### Removed

- **The bot no longer binds the shared `KgsmAuth` section.** It signs nobody in and nothing read the
  binding; who may act comes from the KGSM account store. It still describes the host's Discord
  application on its configuration page, which is a descriptor and needs no binding.

### Changed

- Tracks `TheKrystalShip.KGSM.Auth` 3.0.0, whose `KgsmAuth` section holds a host's OAuth
  applications keyed by provider (`KgsmAuth__Providers__discord__ClientId`).
- **The shared credentials file is `/etc/kgsm/kgsm-auth.env`** — it holds a host's sign-in
  applications, which is what it now says.
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
