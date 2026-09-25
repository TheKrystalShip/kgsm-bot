# The Discord side: where it announces, the board, presence, health, the send queue

**The bot is guild-agnostic.** Nothing in configuration names a Discord server, and inviting the bot
somewhere grants that guild nothing: a guild hears about this host because an admin ran `/setup`
there, and a guild with no row in the store (`../Guilds/CLAUDE.md`) gets nothing whatever the bot's
membership. The `/setup` command surface is `src/KGSM.Bot.Discord/Commands/CLAUDE.md`.

## Announcing

- **One announcement mechanism, with the board as a layer on it.** `AnnounceAsync` iterates the
  configured guilds that follow the server in question and posts once in each; the only variable is
  which channel — the server's own where the guild runs a board and bound one, else the guild's
  announcement channel. `Render` names the server by its label (`**My Factorio** started — … (heisen)`),
  so a message reads correctly out of either.
- **A binding is a preference; the announcement channel is the requirement.** A server's own channel
  deleted in Discord leaves a binding pointing at nothing, and the bot **falls back** to the guild's
  announcement channel rather than losing the announcement — treating the binding as the only place a
  server may report is how every message about it disappears silently for as long as nobody notices.
  The stale binding is not repaired on that path: deciding a channel is really gone takes asking
  Discord, and doing it per announcement would spend a request fixing bookkeeping nobody is waiting
  on. `ReconcileBindingsAsync` does it once, on `Ready`.
- **A binding is dropped only on an answer that says the channel is gone.** The gateway cache answers
  "deleted" and "the bot lost `View Channel`" with the same silence, and unbinding the second orphans a
  live channel full of a server's history with nothing pointing at it. So a cache miss is confirmed
  with a REST fetch — Discord returns nothing for a deleted channel and refuses for one it will not
  show — and **anything other than a clean "no such channel" leaves the binding alone**. A guild the
  bot cannot see at all is skipped entirely, so an outage drops nothing.
- A server with no channel of its own in a guild — every server in a guild running no board, and one
  installed before a board was turned on — reports in that guild's announcement channel.
- **A per-guild failure is logged and the rest proceed**, and the result counts guilds reached against
  the guilds that **follow this server**. A bare success with one guild silently missed is a
  fabricated status; counting a guild that opted out as missed is the same fault inverted, and would
  report a working filter as a partial failure on every announcement.
- **An unreadable filter follows everything.** Reading no rows and failing to read are both "no
  filter": the failure is loud in the log, and a guild an admin set up keeps hearing what it expected
  rather than going quiet for a reason invisible from inside Discord.
- **A server uninstalled is not unfollowed.** The stale row is correct in every case — the guild hears
  nothing about a server that no longer exists, hears nothing about the others it never followed, and
  hears about that name again if it is reinstalled. Dropping the row would empty a one-server list and
  silently switch that guild to following all of them.
- The `Discord:Announce` switches, the status markers and the two message-cleanup keys are **host
  policy, not per-guild**: what this host announces is its own business, and only where each
  announcement lands is a guild's.

## Channels

- **Run state lives in a message, never in a channel name.** Nothing here renames a channel, and
  nothing may: Discord rate-limits channel edits hard enough that a name cannot be kept in step with
  a server's state, and a bot that tries is throttled off the API — losing the announcements too. A
  marker in a channel name is a human's label; the bot's report of run state is the message it posts.
- **A server's channel is named after its id, and its label lives in the topic.** The same rate limit
  rules out following a display name with the channel's name, and the id is also what somebody
  looking for a server's channel scrolls to. The topic is set **on the create call**, so it costs no
  extra request, and rewritten only when a person renames the server — which is the one thing on that
  path driven by a human rather than by a state change.

## An announcement about a server that is down is something you act from

`AnnouncementActions` owns which ones, and the two sets are deliberately different. A **restart
button** goes on `server.crash.exhausted` only — a crash announcement says the supervisor is already
restarting it, so a button there races the supervisor over the same server and blames whoever pressed
it for the attempt that loses. A **thread** opens on both crash kinds, so the conversation about an
incident stays with it; the @-mention surface keys on the channel, which makes a thread its own
context rather than one more voice in the channel's. The button grants nothing: it is a shortcut to
`/restart`, re-resolved against the account store **at the click** (an announcement has no caller to
authorize at the post) and stamped with the clicker's provenance. `Discord:ActionButtons` and
`Discord:IncidentThreads` switch each off; a missing `Create Public Threads` costs the thread and
nothing else.

## The live status message (`StatusBoardService`)

The ambient board, one message per guild. `/setup status <channel>` starts keeping it and pins it;
`/setup status-off` stops, leaving the message standing. It carries every server, whether it is up,
and how to reach it, and is kept current by **editing** — the generous bucket the channel-name version
could not use. Three rules hold it: an event marks it **dirty** and a floor
(`StatusMessageMinIntervalSeconds`) decides when to spend an edit, so a host reboot's fifteen events
cost **one**; a periodic republish (`StatusMessageRefreshSeconds`) is a backstop for what no event
describes, never the mechanism; and the message id is **stored**, because a restart that posts a
second board and keeps the wrong one current is a fabricated status with a timestamp on it. A run
state that could not be read is marked unread, never stopped. The snapshot is read **once for the host
and narrowed per guild**, so a board cannot list a server the guild sitting beside it has unfollowed —
and a guild following none of what is installed is told that, rather than being told the host is
empty. The board prints both a server's label and its id (the label to read, the id to type).

## Joining a guild says one thing, once (`GuildGreeterService`)

A guild with no row hears nothing by design, and from inside Discord that is indistinguishable from a
broken bot — so the greeter posts an introduction on `JoinedGuild` naming `/setup` and who may run it.
System channel, else the first channel it can actually post in, else the owner's DM, each **checked
rather than attempted**; a guild that is already configured is not greeted, because that is a
reconnection and it is already working. It grants nothing: `/setup` still needs KGSM admin.

## The bot's presence (`BotPresenceService`)

`Watching 6 servers · 3 online · 12 playing`, on the bot itself. It reaches a guild that has never run
`/setup`, and one update covers every guild at once, which no other surface here can do.

- **A gateway presence update is not REST, and `IDiscordSendQueue` does not pace it.** The limit is a
  handful per twenty seconds for the whole session. `PresenceRefreshSeconds` is the only thing
  protecting that budget, which is why the line is recomposed on a **fixed tick and never driven by
  an event** — a host reboot must not be able to spend it — and is sent only when it changed.
- **It never claims more than was read.** A host that could not be read says so instead of showing
  the last good numbers; an incomplete count is written as a floor (`3+ online`, `12+ playing`); and
  "0 playing" is never said, because it is noise on a quiet host and a lie on one whose games report
  nobody. The inventory is read separately from the roster for exactly one reason: an empty roster
  means both "no servers" and "could not be read", and those are opposite things to show somebody.

## `IBotHealth` (`BotHealthService`): the failure systemd cannot see

The unit is active, the gateway says Connected, and the bot cannot do the thing somebody just asked it
about — an unreadable account store refuses every command, a missing engine answers none of them, and
neither shows up as anything but a process that is running. `/health` reports it; `/setup show`
answers a different question (what *this guild* is configured with), and the status socket answers
the Control Panel, which is no use to somebody who only has Discord.

The checks run at the moment they are reported: the gateway, the outbound queue, the engine, the event
journal, the KGSM account store, the guild store and the assistant.

- **No check is inferred from another.** They fail independently — the engine and Discord have nothing
  to do with each other — so a summary that took one as evidence for the next would report a state
  that was never measured. A check that throws is a failing check carrying the exception's words;
  nothing here may propagate, because the whole point is to be answerable while things are broken.
- **Four verdicts, not two.** A dependency this host was never given is `Off`, not broken — counting
  an undeployed assistant against the total makes a correct host read as permanently short of
  something. A check that reached no answer is `Unknown`, not a pass, and is deliberately not green: a
  gateway mid-reconnect is neither connected nor faulty, and reporting it either way sends somebody
  the wrong place.
- **The engine is probed by asking it**, not by reading the inventory cache. A cache serves its last
  good answer for as long as its TTL says to, which is right for a cache and wrong for a health check.
  It costs one kgsm process, which is why a person runs this and nothing runs it on a timer.
- **The journal's readability and the age of its newest entry are two facts and stay separate.** A
  quiet host is not a broken one; inferring a fault from silence would call every idle weekend an
  outage.
- It overlaps `GET /status` on three facts (the gateway, the store, the queue) and **cannot disagree
  with it**, because both read the same live objects rather than either deriving from the other.
  Nothing is cached or held between calls.

## Staged restores (`StagedRestores`)

A server name and a backup id together do not reliably fit a 100-character `customId`, and a truncated
one names a *different archive* rather than failing — so a restore is held in `IStagedRestores` and
its button carries 32 hex characters, the same shape the assistant's confirmations use. In memory and
five minutes: a destructive action that survives a restart is one somebody clicks by accident days
later. Who may confirm it: `src/KGSM.Bot.Discord/Commands/CLAUDE.md`.

## One queue out to Discord (`DiscordSendQueue`)

**Everything the bot says unprompted goes through `IDiscordSendQueue`.** Announcements fanning out
across guilds, the status board's per-guild edits, channels created and retired with an install, and
expiring messages cleaned up are four producers with no knowledge of each other, each able to burst.
Rate-limit headroom is a host-wide resource, and being throttled off the API loses everything else
with whichever call spent the last of it — the same failure that makes run state in a channel name
unbuildable. One worker in front of all of it makes them one paced stream.

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
- The backlog is on `GET /status` (`sendQueue`). Connected, configured, every channel visible and
  messages arriving minutes late is a real state whose only symptom is a depth that does not fall.
