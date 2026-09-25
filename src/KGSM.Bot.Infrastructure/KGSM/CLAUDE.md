# The engine side: inventory, events, rosters, backups, history, `/connect`

Everything here reaches the engine through `kgsm-lib` (`IKgsmClient`, `IWatchdogClient`) — never by
shelling `kgsm.sh`.

## Provenance at the chokepoint

`KgsmServerInstanceService` reads `InvocationContext.Current` and stamps `actor`/`origin`
(`discord:<user>` / `discord`) onto every mutating `kgsm-lib` call, so the engine's audit events are
attributable. Outside any scope `Current` is null and kgsm applies its **honest OS-user fallback —
never a fabricated identity**.

## State cache & events

`KgsmStateCache` caches the instance/blueprint inventory (TTL backstop + event-driven invalidation) so
the bot doesn't spawn a `kgsm` subprocess per message; on a refresh failure it **serves the
last-known-good snapshot rather than blanking**.

**The bot tails every producer's journal, not the engine's alone** — a file each has, which any number
of consumers read, so nothing is bound and nothing on any producer's side names this reader.
`KGSM.JournalDir` names the *engine's*, whose location is configurable; the rest are found on disk.
Those events drive both the announcements and cache invalidation via `ServerEventCoordinatorService`.

**Not every announced kind is the engine's to emit.** `server.crashed`, `server.crash.exhausted`,
`server.started`, `server.ready`, `server.restarted` and player presence are the **supervisor's**, in
its own journal. `AddKgsmJournalFederation` must therefore stay registered **after**
`AddKgsmServices` in `DependencyInjection.cs` — above it, the single-journal registration wins, nothing
throws and nothing is logged, and this bot announces installs and backups perfectly while going silent
about every incident. `JournalFederationWiringTests` pins both halves, because that failure has no
symptom from inside a Discord channel.

**It starts at the tail and stores no position.** This surface *announces*, and an announcement is
only meaningful while it is current — replaying a backlog after a restart would post "server started"
for a server that started and stopped hours ago. The federated source keeps one position **per
producer**, so a cursor here would replay each journal independently and post a morning's crashes at
once. Don't give this consumer a cursor to "avoid missing events": the durable record is the journals
themselves, and missing a restart window here costs nothing.

## Announcements: the catalog is the journal's

- **A kind exists only if the journal carries it.** Nothing is polled, derived or inferred to fill a
  gap in the catalog — the bot never reaches into another leaf for a fact the engine does not emit.
  "Update available" is a kind because the engine emits `server.update.available`: kgsm records what
  each update check found and emits only for a version it has not announced before, so a channel sees
  one message per new build however often the host checks. How often it checks is the scheduler's
  business, and nothing here.
- **The reduction happens where the payload's type is still known.** `KgsmServerEventHandler` turns
  each event into a `ServerAnnouncement` — kind, instance, one rendered detail, the actor verbatim —
  so nothing downstream switches over a kgsm-lib event class, and the announcement type does not grow
  a nullable field per event.
- **Announcing is not bookkeeping.** Creating a channel on install and retiring it on uninstall happen
  whether or not anything is announced, so they keep their own registrations. Install announces
  *after* its channel exists; uninstall announces *before* its channel is taken away.
- **Crashes are announced once per crash, not once per restart attempt.** The supervisor emits a
  `server.crashed` per attempt, so a restart loop produces a run of them seconds apart. The first
  attempt is the news; the outcome arrives separately as `server.crash.exhausted`. An unreadable
  restart count is announced rather than dropped.
- **`server.renamed` is subscribed and not announced.** It is the one subscription with no
  announcement kind behind it. Nothing about the server changed, so a channel hears nothing; what the
  event is for is dropping the cached inventory, marking the board dirty and rewriting the channel
  topics, so no surface is left showing a label nobody uses any more.

## Two names: `ServerLabels`

`ServerLabel` (Core) is the one place a server's id and display name are composed; `IServerLabels`
looks a label up for the surfaces that hold an id and nothing else (an event names its server by id;
so does a binding). It reads the cached inventory, so a label costs no kgsm process. **A missing label
is the id, never an invented one** — a server that was never labelled, one the inventory no longer
holds, and an inventory that could not be read all read as the id.

## `IPlayerRoster`: who is on, and the four ways of answering it

`IPlayerRoster` is **the one place a player count comes from** — `/players`, the live status message
and the bot's presence all read it, because two derivations are two numbers that can disagree in front
of the same person. It joins three facts from two authorities: run state from the engine, and both
observability and the live sessions from the supervisor (`IWatchdogClient.GetPlayerPresenceAsync`).

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
  server to build a picture of the host. (`/list` still checks each server itself; it answers one
  person and is not joined to anything.)

## `IBackupInsight`: what was captured, and how good it is

The one place a backup fact comes from, for the same reason `IPlayerRoster` is for players.

- **Consistency is measured per backup and is the thing worth reading.** The engine records how the
  capture was taken: `cold` (stopped — nothing could write mid-archive), `flushed` (running, but it
  wrote its world out first), `hot` (running with no usable save command — **the archive may be
  torn**), or nothing at all when the run state could not be read. A surface that flattens those into
  "backed up" hides the only part that decides whether the backup is worth having. **An unrecognised
  value is printed as it came** — the engine owns this vocabulary, and a surface guessing what a new
  word means is how a torn archive gets described as a good one.
- **Nothing stores an age; the timestamp is stored and the age computed at render.** That is what
  makes the whole-host summary cacheable — a cached timestamp still yields a correct age, where a
  cached age would silently stop counting. The cache is dropped by the engine's own
  `backup created`/`backup restored` events; the TTL is a backstop.
- **A present key with a null value is "read, and has none"; an absent key is "could not look".**
  `LatestAsync` distinguishes them and a renderer must too, and a failed read is deliberately not
  cached.
- **The board flags backups only when they are worth flagging** — past `BackupStaleAfterHours` (48h),
  or never taken at all. An age printed beside every server buries the one that matters among the
  many that do not.

## `IServerHistory`: what happened, out of the durable record

`ServerHistory` wraps kgsm-lib's `IEventJournalHistory`, and `/history [server] [hours]` renders it.
It reads **every producer's journal merged into one time-ordered page**, the same set the
announcements tail: a history that could not show the crash the channel just reported would be the
more confusing of the two failures. **This is a different reader from the announcement one and does
not conflict with the no-cursor rule**: that rule is about the tail. A query answers a question
somebody just asked, and reaching back over a restart is exactly what it is for.

- **Three signals qualify every answer, and all three are carried across unflattened.** An
  **unreadable** journal is not a quiet host — they are the same empty list, and reporting the first
  as the second tells somebody nothing happened on the strength of a permission error. **Coverage** is
  the oldest moment the record still holds, said only when the window asked for more than that.
  **Truncation** is a scan that stopped at its budget, so the page is a prefix. The one empty answer
  that means *nothing happened* is the one with a readable journal behind it.
- **The engine emits far more event types than the bot announces, and an unrecognised one is never
  dropped.** A measured day here carries deploy phases, UPnP forwards, port openings and prune results
  with no announcement kind behind any of them. The types worth naming are named; everything else
  renders from the engine's own word with its subject prefix stripped (`server.deploy.finished` →
  "deploy finished"). That is what makes the phrase table safe to leave incomplete — a type added
  upstream still appears, still names its server and its actor, with no change here.
- **One field is lifted verbatim off each payload**, chosen by a documented order so an event carrying
  several is described by the most specific it has. Nothing is computed, and an event carrying none of
  them shows no detail rather than a stand-in.
- **What a payload field *is* comes from kgsm-lib's `KgsmEventCatalog`; which of them says most about
  the moment is this surface's judgement.** The bot holds no second opinion about which fields
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

## `/connect`: the question a game Discord actually asks

`ServerConnectionService` composes three sources that fail independently — the engine (the instance
and its `Ports`, already canonical `[{start,end,protocol}]`), the host (`HostAddressService`), and the
firewall authority (`FirewallReport`) — and a failure of one is reported on the piece it belongs to.
The ports are worth having when the external IP could not be read; the address is worth having when no
firewall answered. The firewall authority is optional and read-only from here: an unreachable one
costs the "can you actually reach it" line and nothing else.

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

## The watchdog is optional

An unreachable daemon makes `/supervision` report "unavailable" while native start/stop still works.
