# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — a server is called what somebody named it

Every server has two names. Its **id** is what the engine, the channel bindings, the guild filter and
every command key on, it is generated at install, and it never changes. Its **display name** is free
text somebody chose, changed whenever they like with `kgsm instances rename`. This surface shows the
label and keys on the id, everywhere:

- The **live status message** names each server by its label and prints the id beside it in code
  ticks, so the string a command takes is on the line somebody is reading. Rows are ordered by the
  label, which is the order they are scanned in. A server that was never labelled is printed once.
- **Announcements** name the server the way the channel does. The label is read off the cached
  inventory as the announcement is built, so a message and the status message above it cannot
  disagree about what a server is called; the id is not repeated beside it, since the channel is
  named after it and a player-joined line does not need it twice.
- **Autocomplete** shows `Display Name (id)` and matches what is typed against both. What it hands
  back is always the id.
- **`/setup show`, `follow` and `unfollow`** describe each server the same way. The store keeps ids —
  the only thing that survives a rename — and a row naming a server this host no longer has reads as
  its id alone.
- **A server's channel keeps its id-based name and carries the label in its topic.** Discord
  rate-limits a channel rename far too hard for a bot to keep one in step with anything, and the name
  is what somebody scrolls to. The topic is written when the channel is created, which costs no extra
  request, and rewritten when a person renames the server.

**`instance_display_name_changed` is not announced.** Nothing about the server changed — it is up or
down exactly as it was — so a channel hears nothing. What the event does is drop the cached inventory,
mark the status message dirty and refresh the channel topics, so every label already on screen stops
being the old one.

`/install`'s `name` option is the **display name**: the engine generates the id, and the reply says
what the server will be shown as rather than what it will be called.

kgsm-lib pin: **6.1.0**.

### Fixed — an install the engine refused is reported as refused

`/install` and `/uninstall` report the engine's verdict. kgsm answers a mutating call with an exit
code, and it refuses the ones it cannot make — a library it does not know or cannot reach, a name
already taken, a disk with no room on it, a server still running — writing the reason to stderr.
That reason is what the reply carries; a refusal with nothing on stderr says the reason is unknown
rather than passing an empty sentence off as the engine's answer.

This is the same shape every other mutating verb in `KgsmServerInstanceService` already has, so a
result is now read on all of them.

### Changed — a server whose library is away is unread, never stopped

Every surface that reports run state has three answers rather than two: running, stopped, and
unread. A server whose files sit behind an unreachable library is the third, and the reply names
the library — `❔ Unread — library \`external\` is away` on `/list`, `📦 library away (\`external\`)`
on the live status board, its own sentence on `/players` and `/is-active`, and a footer on
`/connect` that declines to say whether anything is listening.

The engine measures an absent disk as an absence and reports null, and kgsm-lib 6.0.0 (pinned here)
carries that through: `InstanceRuntimeStatus.Status` and `Instance.Runtime` are nullable, and
`Instance.LibraryState` says why the rest of an instance is empty.

⚠ **The engine's `is-active` cannot express this, so it is not asked.** It answers by exit code, and
`IsActive` reports any non-zero as stopped — so an instance it cannot even open reads as a stopped
server, and an unplugged disk reads as a shelf of servers going down at once. `PlayerRoster`,
`ServerConnectionService` and `/list` check the library state first and skip the call, which is also
one fewer kgsm process per unreachable instance. The supervisor's presence map is refused on the same
grounds: an entry left over from before the library went away would otherwise render as a measured
zero players.

`ServerRoster` carries the library state and name beside the run state it already carried, so the
board and `/players` read one answer about one moment rather than each asking again.
`ServerConnection.IsRunning` is nullable for the same reason.

An install announcement names the library it landed on (`factorio → external`), from the new
`InstanceInstalledData.Library`.

### Changed — `/install` picks a library, not a path

The command's `path` option is now `library`, autocompleted from the host's registered libraries with
each one's free space beside its name. Absent, the engine resolves placement itself — its configured
default library, else the sole registered one.

The engine has no `--install-dir` flag, so a raw path had nowhere to go: every install through this
command was failing at the engine until this landed. Autocomplete lists only online libraries, because
Discord has no disabled entry and offering an unreachable one would offer a placement the engine
refuses. Free space is omitted rather than shown as zero when the engine measured none — an
unmeasured library is not a full one.

Pins kgsm-lib 5.0.0 for `IKgsmClient.Libraries` and the reshaped `Install`.


## [3.40.0] - 2026-08-18

### Added — every journal line now carries its own id

Every event this bot writes carries an `Id`: a UUIDv7 the shared writer mints per line, inherited by pinning
kgsm-lib 4.41.0. Nothing in this repo changed but the pin.

Why it exists: every durable reference to an event on this host is a byte offset into a named segment,
which holds only while a segment is appended to and deleted whole (conformance §2·l). An id makes a
rewrite **detectable** — a reference carrying both finds the line by position and proves it is the
right one by id, where before a shifted offset resolved to a real, parseable event of the wrong kind
with nothing to notice.

⚠ Optional and optional forever: lines written before this are on disk for as long as retention holds
them, and **absent means unknown, never a mismatch**. Authority: `journal-entry-id-plan.md`.

### Fixed — a first setup on a host where nothing is installed yet completes

`deploy/setup.sh` enables its unit at boot and starts it only when something exists at the unit's
`ExecStart`. A host that has never deployed this project has an empty prefix, so the unit is enabled
and left stopped, and the summary names the unit that is enabled but not running and says
`deploy/deploy.sh` is what starts it. The fresh-host path is `setup.sh` → `deploy.sh` with nothing
in between.

The grant verification adapts with it, and still makes two real polkit-gated calls: `daemon-reload`,
plus one `manage-units` call on this project's own service — `start` when the service is running
(systemd queues a no-op job), `try-restart` when it is not (documented to do nothing for a unit that
is not running). Both are dispatched as the same `manage-units` action, so a host without the grant is
refused either way and the probe measures the grant rather than the unit.

⚠ Measured in the positive direction only. The deploying user on the development host is in
`wheel`, and two pre-existing polkit rules there grant that group every
`org.freedesktop.systemd1.*` action outright, so no systemctl call by that user can be refused
and the negative path cannot be exercised on it. That `try-restart` consults polkit before it
decides there is nothing to do is systemd's own dispatch order, not something this host can
demonstrate.

## [3.39.0] - 2026-08-17

### Changed — a step's label comes off the wire

`AssistantToolVocabulary` held a table of tool names and their prose. The assistant's catalog is a
file on the assistant's host, so this repo learned of a rename only by being rebuilt — and every
name in that table was stale, showing nothing while the fallback quietly rendered each tool's raw
name instead.

The assistant sends each step's label with the step, and that is what a Discord thread now shows. A
frame carrying no label still describes the step from its own name, because a step dropped for being
unrecognised makes the account of a turn quietly incomplete.

## [3.37.3] - 2026-08-16

### Fixed — federation cannot be registered in the wrong order

kgsm-lib 4.30.0 makes `AddKgsmServices` and `AddKgsmJournalFederation` register the same resolution
rule, so either call order yields a federated reader. ⚠ **The bug it removes had no symptom**: a
consumer that federated too early kept reading the engine's journal *successfully* — healthy journal,
quiet host, nothing to catch — while every other producer's events sat in files it never opened.
`JournalDiscovery` also scans once per process now, instead of once for the history reader and again
for the live tail.

## [3.37.2] - 2026-08-15

### Changed — what the recogniser is primed with is the speech package's answer now

`SpokenVocabulary` is gone from Core; it is `TheKrystalShip.KGSM.Speech.SpokenVocabulary` (1.3.0),
which the assistant leaf reads the same rules out of so a voice note sent from a browser is primed
with the servers a spoken request is primed with. What stays here is reading the inventory and
deciding how often to re-read it — the part that is genuinely this surface's.

## [3.37.1] - 2026-08-15

### Changed — where a spoken reply is cut is the speech package's answer now

`SpokenSegmenter` and `SpokenText` are gone; both jobs are
`TheKrystalShip.KGSM.Speech.SpokenSentences` (1.2.0), which the assistant reads the same rules out of.
Two surfaces deciding separately where a reply is cut decide it differently within a release, and this
one and the assistant's had already drifted on four rules before either shipped.

What changed for a listener here, all of it the package's rules winning:

- **A short sentence is spoken sooner.** The floor for a piece is 24 characters rather than 40, so
  "Factorio is running normally now." is heard immediately instead of waiting for the sentence after
  it. A complete sentence of six words is the answer, and the floor exists only to stop "Yes." being
  its own recital.
- **A link is read as its text.** Its target is an address, not a sentence — "example.invalid/path"
  read aloud is noise, and its dots would cut the sentence in the middle.
- **A table's pipes and a link's brackets are silent**, as the other markup already was.
- ⚠ **A fence marker counts only at the true start of a line.** A sentence ending part-way along a
  line leaves the rest of it in a fresh buffer, and reading that as a line start took
  "Done. \`\`\`yaml" for a fence — silencing every word after it for the rest of the answer. Both
  implementations had this; neither had noticed.

`SpokenText`'s eight assertions each hold against `SpokenSentences.Whole` unchanged, and moved into the
package's suite with the rest — it had no rule the package lacks, and keeping it would have kept a
second, weaker stripper on the same surface as the first.

## [3.37.0] - 2026-08-15

### Added — a spoken answer starts before the assistant has finished writing it

A voice turn now puts its question over the assistant's frame stream and reads the reply out sentence
by sentence as it arrives, instead of waiting for the whole turn and then synthesising the whole
thing. On a long answer that is most of the wait removed: the room hears the opening while the model
is still producing the end. Nothing about the answer changes — the whole reply is spoken, in order,
markup stripped — only when each part of it goes out.

- **`SpokenSegmenter`** (Core) holds token fragments until there is a whole sentence and hands it
  over. A boundary is a full stop, question mark or exclamation mark *followed by whitespace*, or a
  line ending, and it must be worth at least `LeastChars` of speech — so "Yes." rides out with the
  sentence after it and "2.0.58" stays in one piece. Whatever the reply ends on is spoken by the
  flush, terminated or not.
- ⚠ **No boundary is offered inside a fenced block.** Segmenting first and stripping markup per piece
  would read a stack trace out a line at a time, which is precisely what `SpokenText` exists to
  prevent. The fence is tracked across the slices as they accumulate; the piece finally cut carries
  the whole block and `SpokenText` drops it, including a fence the reply never closes.
- **`SpokenRecital`** (Discord) synthesises and plays the pieces through a single reader, so they are
  heard in the order they were written whatever order two waiters would have been released in. It
  waits for the acknowledgement to finish before the first sentence, so the two are never said over
  each other.
- **Everything spoken for a turn rides one recital**, including the sentence pointing at a staged
  action's buttons and the sentence said when a turn fails — anything said beside it would survive an
  interruption and talk over whoever cut in.
- **No new setting.** `Voice:Speak` is still the only gate on speaking at all, and with it off there
  is no recital and the turn takes the buffered reply exactly as a typed question does.

### Changed — cutting the bot off drops the whole answer, not the sentence in the air

An answer read out as it is written is a queue of sentences, so the moment somebody says the trigger
is as likely to fall in the gap between two of them as inside one. Stopping only what is playing had
the bot pause and then carry on talking over the person who cut in, which is worse than not letting
them cut in at all.

- `IVoiceSessions.BeginRecital` opens one answer's worth of speaking and `IVoiceRecital` is the handle
  its pieces are spoken through. `StopSpeaking` abandons the current recital **whether or not anything
  is playing**, and every piece still owed to it is refused.
- ⚠ A piece is checked twice and **the check that matters is the one after the wait for the mouth** —
  a sentence queued behind another spends that sentence's whole duration waiting there, which is
  exactly the window an interruption lands in. Refusing only on the way in would let everything
  already waiting play.
- `StopSpeaking` reports true for a recital abandoned with nothing in the air, because there really
  was something to drop.

### Changed — the turn client's streaming seam carries the reply, not only the steps

`AssistantStream` names the two things a surface can watch: `Steps` (each tool call as it starts and
finishes) and `Reply` (slices of the answer as they are written). Watching neither takes the buffered
body, which is what a Discord message is. The `text.delta` frame is read for the first time here; the
existing activity overload is the same call with `Steps` filled in, so the @-mention surface and
incident triage are untouched.

## [3.36.0] - 2026-08-15

### Changed — speech is a leaf now, not a worker this bot starts

The models moved out of this repo into **kgsm-speech**: one engine per host, socket-activated at
`/run/kgsm-speech/speech.sock`, serving every surface that listens or speaks rather than a child
process this bot owns. Nothing about the bot's memory changes — it was already 145MB idle — but the
engine is no longer the bot's to start, to configure, or to keep.

- **`TheKrystalShip.KGSM.Speech`** (the client and the wire contract) replaces the Whisper.net,
  KokoroSharp and ONNX Runtime references. `HostSpeech` owns the connection; `LeafSpeechToText` and
  `LeafTextToSpeech` are what the rest of the bot asks. `ISpeechToText`/`ITextToSpeech` keep their
  shape, so nothing above the seam changed.
- **The engine owns the voice.** Requests name no voice, which is what makes a person hear the same
  assistant in Discord as anywhere else on the host. `/voice speak-as` therefore changes it **for the
  whole host** and now says so.
- **The bot no longer decides how long the models stay loaded.** It says speech is about to be wanted
  when it joins a channel, and that is all: a bot leaving a channel is not evidence that nobody else
  is speaking, and the engine idles out on its own schedule.
- **A host without the leaf is the ordinary case** — the bot joins, hears nothing, and answers in the
  channel's chat, exactly as it did on a host with no model files.

### Removed — six settings that belong to the engine

`Voice:ModelPath`, `Voice:UseGpu`, `Voice:SpeechModelPath`, `Voice:SpeechVoice`, `Voice:SpeakUseGpu`
and `Voice:WorkerIdleMinutes` are gone; `Voice:SpeechSocket` (blank = the standard path) replaces
them. ⚠ An existing `Discord__Voice__SpeechVoice` override — the Control Panel writes one to
`/var/lib/kgsm-api/leaf-overrides/bot.env` — now binds to nothing. Set the voice on the **Speech**
leaf instead.

`deploy/setup.sh` no longer fetches the two models (813MB); kgsm-speech's does, adopting the files
this project left in `/var/lib/kgsm-bot/models` rather than downloading them again.

## [3.35.0] - 2026-08-15

### Changed — the speech models moved into a worker process

The bot held 1.8GB resident while doing nothing. Measured on hotrod by starting the deployed binary
with the voice knobs flipped:

| configuration | host RSS | VRAM |
|---|---|---|
| `Voice__Enabled=false` | 145MB | — |
| + whisper on the card | 505MB | 570MiB |
| + kokoro on the card | 1790MB | 1148MiB |
| both on the processor | 1154MB | — |

The bot is 145MB. Everything above that was two models loaded at startup whether or not anybody had
ever been in a voice channel.

⚠ **Unloading them inside the process does not work**, which is why this is a second process rather
than lazy loading. Probed directly — load, dispose, aggressive compacting GC, `malloc_trim`, measure:
whisper returned **9MB of 383MB**, and kokoro **331MB of 1319MB**. The video memory does come back
(682 → 122MiB). What does not is the CUDA runtime: 919MB of the kokoro figure appears on the *first
sentence synthesised*, when cuDNN pages in its kernel libraries, and that belongs to the process until
it exits. Reloading and disposing a second time plateaus at the same number.

So `SpeechWorker` starts **this same binary with `--speech <socket>`** when the bot joins a voice
channel. Not a second artifact and not a second version to keep in step: the worker reads the settings
file the bot read, so a Control Panel override reaches the models with nothing forwarded. `ISpeechToText`
and `ITextToSpeech` are unchanged, so nothing above the seam knows there is a process involved.

- **The bot listens and the worker connects back.** No window where the socket does not exist yet, and
  nothing polling for a file to appear.
- **The worker exits when the connection closes** — asked to stop, or the bot dying. An orphan holding
  a gigabyte and a slice of the card is what this design exists to prevent.
- **It stays loaded once started.** Loading is ~3s and the first sentence after that is slower again;
  `Voice:WorkerIdleMinutes` (default `0`) trades those seconds back for the memory on hosts that want
  it. A bot that never joins a voice channel never starts one either way.
- **The worker keeps no state.** The names to expect and the voice to speak in travel with every
  request, so priming, echo detection, the phrase cache and the 24kHz→48kHz upsample stay bot-side —
  and the picker, `/voice speak-as` and the leaf setting all work with no worker running, because the
  voices are files this process can read.
- **A worker that will not start is not an outage**: the bot listens and answers in the channel's
  chat, the same shape as a host with no model files.

### Added — `Voice:WorkerIdleMinutes`

How long the models may sit loaded after the last voice channel empties. Zero keeps them.

## [3.34.0] - 2026-08-14

### Fixed — 61MB held to speak in one voice

⚠ **`KokoroVoiceManager` bulk-loads every voice on disk** the first time it is asked for one, and that
is not the 54 English voices it looks like — it walks subdirectories, and `voices-zh` brings the total
to **157 `.npy` arrays**. Measured on hotrod: **1848MB → 1787MB** resident after loading exactly the
one voice in use. Each array is over the large-object threshold, so they sat on the LOH and were never
compacted away.

`KokoroVoice.FromPath` reads a single voice — about half a megabyte and a few milliseconds. A fresh
process now holds only the configured one, and `/voice speak-as` reads whatever it is asked for, so
memory follows what has actually been tried rather than what exists. Listing the available voices
reads the **directory** and loads nothing at all.

⚠ A voice named in configuration that this host does not have now reports the names it *does* have,
rather than failing with the name that was already known to be wrong.

### Changed — `af_heart` is the default

The best-graded voice Kokoro ships, and the one this host speaks in. The picker leads with it, since a
dropdown opening on its current value reads as a setting rather than as a list to hunt through.

## [3.33.0] - 2026-08-14

### Added — `/voice speak-as`, which swaps the voice without a restart

The panel's picker restarts the bot, because leaf config is delivered as environment variables and a
process cannot be handed new ones while it runs. That is the mechanism, not this field: nothing about
a voice needs a restart. Every voice is already in memory — they load together on first use, and the
synthesiser takes one *per sentence* — so changing it swaps a reference. Nothing reloads, nothing
re-warms, and the bot stays in the channel mid-conversation.

⚠ **It lasts until the process does, and the reply says so.** The durable setting stays the leaf's,
and this deliberately does not write to it: a bot speaking in a voice its own configuration does not
name is two sources of truth, and the invisible one would be winning. Hear a voice here, keep it in
the panel.

Autocomplete rather than fixed choices — Discord caps an option at 25 and there are more English
voices than that. Suggestions come from the voices actually loaded, so the list cannot offer a name
the host would then refuse.

⚠ Changing the voice **clears the phrase cache**, which is keyed by text and therefore holds audio in
the voice being replaced. Without that the bot answers in the new voice and goes on acknowledging in
the old one.

## [3.32.0] - 2026-08-14

### Changed — the speaking voice is a dropdown

`Voice:SpeechVoice` declares its values, so the Control Panel renders a picker instead of a text box
somebody has to know Kokoro's naming to fill in.

The **English** voices, listed best-first within each accent. Kokoro ships voices for eight other
languages and they sit on disk beside these, but they expect text in those languages — offered here
they would be twenty-odd ways to read an English answer badly. Anything Kokoro can load still works if
it is set directly; the list is the set worth choosing from.

Ordered by training data rather than alphabetically, because that is the axis you can hear: the
difference between the top of a group and the bottom is not accent or timbre but how synthetic the
voice sounds.

⚠ Applying it **restarts the bot**, which drops it out of any voice channel it is sitting in.

## [3.31.0] - 2026-08-14

### Added — clearing and compacting a channel's conversation

`/conversation clear` and `/conversation compact`, and out loud: **"hey assistant, start over"**,
"forget everything", "clear the conversation", "compact the conversation".

A channel's conversation is shared and never ends, so without this it only ever grows. Clearing a
shared one needs **operator** — it takes the memory from everybody in the channel, and they are not
the person who asked — which the assistant decides and refuses; the refusal is shown and spoken
exactly as it arrives. Compacting stays open to anyone, since the people who notice a conversation
getting long are the ones talking in it.

**Read before a turn exists, not asked of the model.** A model told to forget the conversation replies
that it has and remembers every word: a reply is all a turn can produce, and the reply is not the thing
being asked for. The spoken phrases are matched deterministically, like the yes/no reader and for the
same reason — something that discards a room's memory should be readable, testable and identical every
time.

⚠ **Matched against the whole utterance, not by containment.** *"the server didn't start over the
weekend, can you check"* is a question, and a matcher looking for "start over" anywhere would answer it
by wiping the channel. "Forget it" is left out entirely: it almost always means "never mind about
that", said in the exact moment somebody would be most annoyed to lose everything.

### Changed — a British voice

`Voice:SpeechVoice` is `bf_emma`. Kokoro's British voices vary widely in how much speech they were
trained on and it is audible; this is the only one with hours behind it rather than minutes.

## [3.30.0] - 2026-08-14

### Added — cutting the bot off mid-answer

Saying the trigger phrase while the bot is talking stops it, and the request that stopped it is
answered next. Now that a whole reply is spoken, a long one was something you had to wait out.

**Only the trigger does it.** A voice channel is a room where people talk over each other, and the
cheap signal — somebody starting to speak — is in the pipe already and free, at about twenty
milliseconds against the second and a half a trigger match costs. It was rejected anyway: acting on
it would silence the bot every time two people spoke at once. Cutting in has to be deliberate, which
is the same bargain the trigger already is for being heard at all. Continuing a conversation is not
cutting into one, so neither an answer to a question the bot asked nor the rest of a request cut off
at the ceiling stops anything.

It is spotted twice for one interruption — mid-sentence by the early look-ahead, then again from the
finished sentence — and the first to find an answer playing is the one that stops it. The
mid-sentence path is allowed to act, alone among the things it may do, because stopping an answer
undoes nothing: the reply is already in the chat and the turn behind it has finished. Being slow
about it is the entire failure.

Switched with `Voice:Interruptible` (on).

### Fixed — an interrupted answer really stops

Cancelling the write stops the writing, not the sound: up to a whole buffer of audio is already
queued and plays on for about a second. The output stream is now thrown away with what it was
holding, and the next answer builds a new one.

⚠ The writer's own `ClearAsync` cannot be used for this — it dequeues frames without releasing the
slots they occupied or returning their buffers to the pool, so one call starves the stream
permanently. Discarding queued audio means discarding the stream.

The stream is also now **disposed** on every path that abandons it, rather than having its reference
dropped. A `BufferedWriteStream` owns a loop that goes on pacing frames onto the connection until
something cancels it, so letting go of one left a second writer running against the same connection
as its replacement.

## [3.29.0] - 2026-08-14

### Removed — the cap on how much of an answer is spoken

`Voice:SpeakMaxCharacters` (400) cut a spoken reply at a sentence boundary and left the rest to be
read. The whole reply is now spoken, with only the markup stripped.

A cap here decides that somebody has heard enough, which is not a judgement this surface is in any
position to make — and it leaves the answer in the room disagreeing with the answer in the channel,
with nothing anywhere to say which one was the answer. **How long a reply runs belongs to the
assistant**: a spoken turn already asks for one written to be heard (`ReplyStyle.Voice`), and that is
the lever to reach for if answers run long.

The key is gone from the settings file, the options class and the generated descriptor. An
environment variable still setting `Discord__Voice__SpeakMaxCharacters` now binds to nothing, which
is harmless.

## [3.28.1] - 2026-08-14

### Fixed — a dropped voice connection was reported as a wedged audio stream

The bounded write added in 3.26.1 catches a cancelled write and calls the stream unusable. But
Discord.Net also cancels an in-flight write from the **audio client's own** token when the voice
connection goes away — measured here six seconds into an eleven-second answer, immediately followed
by `Audio #1: Disconnecting`. Both arrive as the same exception, and the log named the wrong one:
*"the audio stream stopped accepting speech"*, for a connection that had simply gone.

The two are now told apart by whether the budget actually elapsed, which is the thing that
distinguishes them. A real timeout still rebuilds the stream and warns; a connection that went away
says so, at information level, because it is not a fault in anything the bot owns. Reporting a cause
that was not measured is the same error as reporting a status that was not measured.

## [3.28.0] - 2026-08-14

### Changed — one note that bends, instead of two notes

⚠ **Two struck notes a fourth apart is a doorbell**, however the timbre is tuned. The melody is the
thing being recognised, and everybody already knows that one — so no amount of adjusting the partials
was going to fix it.

There is one note now, and the direction is carried by **bending its pitch** — A4 up to B4 to open,
B4 down to A4 to close. Which is the more natural way to say it anyway: a voice rises at the end of a
question and falls to finish a statement, and that is exactly what these two mean. A whole tone is
enough to hear as inflection and small enough not to become a tune.

The timbre moves from bell to **soft mallet on wood**: a body resonance beneath the fundamental, the
3.9× partial that makes wood sound like wood, a mallet click gone in thirty milliseconds, and the
metallic inharmonic partial deleted. Measured, it is bright at the strike and settles to near-pure
within a fifth of a second — a warm knock rather than a ring. The reflections are pulled in tight, so
it sounds close rather than in a hall. Attack is 28ms.

⚠ Phase is **accumulated per sample** rather than computed from elapsed time. With a frequency that
changes, `sin(2πf(t)·t)` is not a note that bends — it is a note whose phase jumps every sample, which
is heard as a rasp.

## [3.27.0] - 2026-08-14

### Changed — the tones are struck notes rather than beeps

The first pair read as electronic, and measuring them says why: a tone's brightness — the share of its
energy above the fundamental — sat at 0.0117, where a **pure sine at the same pitch measures 0.0105**.
The partials were there but so quiet that what came out was a bare sine with a decay on it, which is
what a beep is.

Three things now do the work, and dropping any one is audible. The upper partials **decay faster than
the fundamental**, so the note starts bright and mellows as it rings — that is what a struck object
does and what a synthesiser does not. The partials sit **slightly off the exact harmonic ratios**,
because perfectly integer overtones are a property of arithmetic rather than of anything physical, and
a near-unison a fifth of a hertz out beats slowly against the fundamental for warmth. And the attack
is a **raised cosine over 20ms** instead of a 6ms ramp, since a straight line into full amplitude is
heard as a click on the front of the note. A few quiet delayed copies put it in a room rather than
injecting it dry into the stream.

**The ring is now a full second, and that is free.** The output stream will not transmit less than a
second whatever it is given, so a 290ms tone and a 1s tone occupy the connection for exactly as long —
the only question was whether the remainder was silence or decay.

Level is measured after rendering rather than budgeted from the constants: seven partials, two
overlapping notes and four reflections sum to something nobody can predict by reading them, and
guessing wrong means either clipping or a tone too quiet to do its job.

### Changed — the opening tone arrives sooner, and never arrives late

Three separate causes of a tone that lags the moment it describes:

- **The look-ahead ran on the 200ms tick.** It now runs on the loop that receives frames, so a
  sentence is read the instant it is long enough rather than up to a fifth of a second later.
- ⚠ **A tone that would arrive late is now dropped.** It used to queue behind whatever was already
  playing. "Your turn" heard three seconds after the fact describes a moment that has gone, and
  invites somebody to talk into something not listening for them — worse than silence. It waits 400ms
  for the connection to be free and gives up.
- **Contention is now visible.** The early read is skipped whenever the recogniser is busy, which in a
  room with several people talking is often — and the only symptom was a tone that seemed late. That
  skip is logged, so it can be counted instead of guessed at.

## [3.26.1] - 2026-08-14

### Fixed — audio shorter than a second wedged the voice surface permanently

**Measured on this host:** the bot joined a channel, heard a request perfectly, transcribed it, put
it to the assistant — and then said nothing, ever again. It kept recognising speech the whole time.
Nothing was logged as an error, the gateway stayed Connected, and the assistant answered normally.
Every request made afterwards vanished.

The cause is in Discord.Net's buffered audio writer: it transmits **nothing** until its queue holds a
full buffer's worth of frames, which at the default is a whole second of audio. Below that its
sending loop waits for a buffer that will never fill, and `FlushAsync` waits on the sending loop — so
a short write neither completes nor throws. It hangs. The 290ms tone introduced in 3.26.0 was
fourteen frames against a fifty-frame buffer; it was the first thing that connection had ever been
asked to say, so the very first write wedged. Because spoken requests are answered one at a time,
every later request queued behind a task that was never coming back.

Audio below the buffer length is now padded with silence — the sound plays at its proper moment and
the padding costs only the time it takes to drain. The buffer length is stated where the padding is
derived from it, since the two disagreeing is what causes this. The write is also **bounded**: audio
taking longer to go out than it could possibly take to play drops the stream and rebuilds it, so no
future stall in the audio stack can freeze everything a person asks afterwards.

⚠ This was latent before the tones: a short spoken answer — "Yes." — synthesises to well under a
second and would have done the same thing.

## [3.26.0] - 2026-08-14

### Added — the listening state is two tones, and one of them arrives mid-sentence

A voice surface has two moments that are pure state and carry no content: the bot is waiting for you
to speak, and the bot has your request and is working. Both were being reported by talking — "Looking
into it" — which is information the first few times and noise by the twentieth, the same fatigue that
makes a wake phrase tiresome to repeat. Both are now a tone: **rising to open, falling to close**, the
same two notes (G5 and C6) in opposite order, which is the convention every device a person already
owns uses and so needs no explaining. They are synthesised rather than shipped — a fundamental with
two quiet partials and a struck-bell envelope — so there is no binary asset and the pitch is four
constants. Peak amplitude is a fifth of full scale, deliberately quieter than the answers, and a tone
costs no synthesis, so it starts playing immediately instead of after a model has produced a waveform.

**Anything with something to tell you is still spoken.** A long-running job, a yes that could not be
made out, a refusal — a tone cannot say *why*, and a rising tone alone after a failed confirmation
would read as "go ahead" when the opposite happened.

**The trigger is now spotted before the sentence is finished.** Recognition runs on a *closed*
utterance, so the earliest the bot could previously know it was being addressed was after the speaker
had stopped — which put the tone meaning "go ahead" after the words it was meant to encourage. The
assembler now hands out a copy of a sentence's opening once it holds 1.5 seconds of it, and the
trigger is looked for there, so "hey assistant, is minecraft running" is answered with the tone while
the speaker is still on "minecraft".

⚠ **A partial reading may only make a sound — never a decision.** It is the opening of an instruction
nobody has finished giving: nothing is dispatched, counted, or opened from it, and the same audio
arrives again complete a moment later. That is what makes it safe for whisper to be wrong about a
fragment, which it sometimes is. It is also **skipped outright when the recogniser is busy** rather
than queued, so a look ahead at an unfinished sentence can never delay somebody's finished one. It
costs a whole recognition pass on most of what is said in the channel, addressed to the bot or not —
whisper pads to a fixed window, so 1.5 seconds costs about what a sentence costs. `EarlyTriggerMs: 0`
turns it off.

Two new keys, both on by default: `Discord:Voice:Chimes` and `Discord:Voice:EarlyTriggerMs`.

## [3.25.0] - 2026-08-14

### Fixed — a dead encrypted session is detected and rebuilt

Measured on this host: one speaker's stream failed to decrypt at **fifty packets a second for over
two minutes** — 2,142 failures on one stream, 291 on another — while the connection stayed Connected
and nothing was logged as an error. Every frame was dropped before reaching the recogniser, so the
bot heard one request and then nothing at all. From inside the channel it looked like the bot had
died. The documented baseline for this is a few percent of frames; this is the same failure at a
hundred.

`VoiceDecryptHealth` watches both halves, because either alone means nothing: failures happen
normally while the group re-keys, and no frames at all is just a quiet room. Failures arriving **and**
nothing getting through, over a ten-second window, is the keys being wrong. The failures are read out
of Discord.Net's log stream, which is the only place that fact exists — there is no event and no
counter for it.

The only lever is a reconnect, because the keys are negotiated during the handshake. So the bot
rebuilds the connection, up to three times, and then **leaves the channel** rather than sitting in it
deaf — a bot present and hearing nothing is what made this hard to notice. A session that then works
gets its full allowance back.

### Changed — one spoken yes approves everything the turn staged

"Uninstall both starbound instances" stages two actions, and the previous rule offered voice
confirmation only for exactly one — so the spoken yes went nowhere and the instruction to use the
buttons was buried twelve seconds into a reply. A person who asked for both and then agreed has
answered about both.

The offer now names them first: *"That's 2 things: uninstall starbound and uninstall starbound-78.
Say yes to do all of them, or no to cancel."* — so a yes is never about a set nobody heard described.
Each grant is still redeemed on its own, because the assistant judges authority and validity per
action and one refusal says nothing about the next. The outcomes are posted individually and spoken
as one line with a count, since somebody who approved out loud is not reading the screen.

## [3.24.1] - 2026-08-14

### Fixed — "connected but receiving nothing" is now visible

A voice session was measured holding a channel for three minutes while somebody repeated the trigger
phrase, and **not one audio packet ever arrived**. Everything on this side reported itself healthy:
the connection said Connected, no warning was logged, and the surface looked identical to a bot that
hears you and does not understand you. The two are opposite problems and the fix for one does nothing
for the other.

Frames are now counted. `/voice status` leads with the fault when there are people in the channel,
time has passed, and nothing has been received, and names the causes — all of which are outside this
process: the bot **server-deafened** in the guild, microphones muted or on push-to-talk, or a voice
server that handed out a session and routed no media. The log says it once per session, as a warning.

Nothing is done about it automatically. Reconnecting on its own would fix the third cause and hide
the first two, and a bot that silently rejoins is a bot whose real problem is never found.

## [3.24.0] - 2026-08-14

### Added — it says something while it works

Silence reads as broken. An assistant turn takes seconds, and a confirmed install was measured on
this host at **32 of them** — 17:05:09 to 17:05:41, every one silent after somebody said "go ahead".
From inside a voice channel that is indistinguishable from a bot that never heard you, and the
natural thing to do is say it again.

So a request now gets an immediate spoken acknowledgement — *"Looking into it."*, *"One moment."* —
and an approved job says so as it starts. A job that moves files around (install, uninstall, update,
backup, restore) says it will take a while; starting and stopping do not, because the warning would
be longer than the wait.

**It costs no latency, because it is said alongside the work rather than before it.** The turn is
started first and the phrase plays while it runs; the output stream serialises writes, so the answer
queues behind the acknowledgement instead of overlapping it. Awaiting it before starting the work
would have added a second to every request in order to appear faster.

Short phrases are kept as the audio they became, so the second time one is said it is not synthesised
again — a phrase whose whole value is arriving immediately should not pay to be generated. Bounded to
a couple of dozen short entries; whole answers are never cached, since nobody hears one twice.

The phrases vary. One that never changes stops being heard as a reply and becomes a noise the bot
makes, and a person who has stopped listening to it has lost the thing it was for.

`Discord:Voice:Acknowledge` switches it off.

## [3.23.0] - 2026-08-14

### Added — approving a staged action out loud

A single staged action can be approved by saying so, for hands that are in a game. The spoken offer
becomes *"Say yes to go ahead, or no to cancel."* and the buttons are posted exactly as before —
nothing is taken away, and both redeem the same grant, so whichever happens first wins and the
assistant refuses the other. `Discord:Voice:ConfirmByVoice` switches it off.

**Three outcomes, and the third is the point.** `SpokenIntents.Read` answers affirm, decline, or
unclear, and only an unmistakable yes approves. A binary reading would have to resolve a misheard
word into one of two answers, and half the time it would resolve it into approval.

- **Agreement is a vocabulary, not a password**: "yes", "yeah", "go ahead", "do it", "send it",
  "sounds good", "please do", "of course", "make it so" and a few dozen more. A phrasing that is
  missing costs one repeated question and never a wrong action.
- **The two directions are judged differently on purpose.** A yes must be short and plainly about the
  question — "yeah, go ahead" approves and "yeah, I was telling him about the minecraft thing" does
  not. A no is allowed to be longer, because a wrongly-read no can simply be asked for again and a
  wrongly-read yes cannot be taken back.
- **A hedge is not consent, however it leans.** "Probably", "I think so" and "up to you" are the
  answer of somebody who has not decided.
- **Grunts are excluded deliberately.** "Uh-huh" means yes and "uh-uh" means no, one vowel apart over
  a noisy channel, and a recogniser that confuses them confuses them toward approving. Only words
  approve.
- **No model in the approve path.** A model generalises better over phrasing and is also a thing that
  can be talked into approving; what decides whether something is destroyed should be readable,
  testable and identical every run.

**It never fake-confirms.** Anything not unmistakably a yes is said out loud as not understood and
asked again — *"Sorry, I didn't catch a clear yes or no about stop minecraft. Say yes to go ahead, or
no to cancel."* — up to three attempts, after which the prompt in the chat is left standing rather
than the bot asking forever.

**Only ever for one staged action.** With two of them a spoken yes does not say which, and picking
one would be inventing the half of the instruction nobody gave; two offers stay with the buttons that
name what each does.

Two routing rules keep it from swallowing ordinary speech: saying the trigger out of habit —
"hey assistant, yes" — still answers the question rather than starting a new one, and asking the bot
something else instead of answering abandons the offer rather than being misread as a reply to it.

## [3.22.0] - 2026-08-14

### Added — answering the bot's own question needs no trigger

Measured in a live session: the assistant asked something and answering it took *"hey assistant,
yes"*. The bot had spoken to that person a second earlier and still made them re-introduce
themselves. The trigger exists to tell the bot's conversation apart from the room's, and immediately
after it has asked somebody a direct question there is nothing to tell apart.

So when a reply ends in a question, the bot waits for that person's next utterance and takes it as
the answer. `Discord:Voice:ReplyWindowSeconds` (20, `0` disables) bounds the wait.

Three properties keep it from becoming an open microphone:

- **One speaker, one channel.** The window belongs to whoever was asked; everybody else in the room
  is unaffected, which matters in a channel where a measured 84 utterances produced 10 requests.
- **Spent by a single utterance**, answer or not. It cannot accumulate, and a reply that asks a
  further question opens the next one.
- **Never opened while an action is staged**, even when the assistant's reply ends in a question.
  What the bot wants at that moment is a button press, and a listening window beside a pending
  confirmation invites somebody to approve by saying yes — which is not what would happen. The
  buttons remain the only way to approve.

It changes who has to say the trigger and nothing about what anybody may do: an utterance let through
is put to the assistant exactly as a triggered one is, with the speaker's own authority re-derived at
the turn.

## [3.21.0] - 2026-08-14

### Added — a spoken request asks for a spoken answer

The assistant takes an optional per-turn `style`, and the voice surface sends `"voice"` with it. The
@-mention surface sends nothing and keeps the full written answer: the style describes where a reply
lands, not who asked for it, and the same account asking the same question from a text channel still
wants the paragraph. `style` is omitted rather than sent as `"default"`, so an assistant too old to
know the field receives exactly the body it always did and nothing has to be deployed in an order.

Measured through the deployed service on the relay path. The gain is not where it looks like it
should be — a question already answered in one line barely moves:

| asked | written | spoken |
|---|---|---|
| is minecraft running? | 50 | 46 |
| how much memory is Ketchup using? | 42 (`4 GB`) | 39 (`4 gigabytes`) |
| what servers do I have? | 224, with `*` and `**` bullets | 94, one sentence |

The verbose answers are the ones that move, and the markup mattering more than the length: a synthesiser
reads `*   **Ketchup** (Palworld)` out as punctuation.

### Changed — the two halves of a staged offer stop repeating each other

The assistant now says an action is waiting for confirmation, and this surface said it again on the
way to the speaker. That an action is staged is the assistant's to report; that the thing to press is
in the channel's chat is only this surface's, because the assistant knows nothing about the buttons.
So the spoken addition is now about *where* — "Approve it in the chat." — and grows back into the
whole sentence only when the assistant said nothing at all, which is the one case where nobody has
been told there is anything waiting.

## [3.20.0] - 2026-08-14

### Added — the recogniser is told what this host's servers are called

Whisper knows English, not this host. A server called `Ketchup` came back as "catch-up" and
`projectzomboid` as "Project Sambite" — correct readings of the sound and the wrong answer, and no
amount of audio quality fixes it. `SpokenVocabulary` composes the trigger phrase and the host's
instance and blueprint names into the prior context whisper is conditioned on, refreshed from the
inventory every couple of minutes so a server installed is heard about without a restart.

Measured on the same synthesised phrases with and without it, so the audio was identical and the
prior context was the only variable: **four of eight names recognised, against seven of eight**.
`Ketchup` and `projectzomboid` both came right, at no cost in latency. `necesse` still comes back as
"nessus".

Nothing downstream rewrites what anybody said, and correcting a misheard name after the fact was
measured to be impossible rather than merely risky: `nessus` sits at 0.43 similarity to `necesse`,
*below* `restart` → `romestead` at 0.44 and `stations` → `stationeers` at 0.73. Every threshold that
catches the real correction also rewrites the verb in half the commands people speak. A name that
survives all this is left as it was said, for the assistant to ask about.

Priming has a failure of its own — given audio with nothing recognisable in it, whisper sometimes
continues the context instead of returning nothing — so a transcript that is a run of the context
reproduced in order is discarded and counted. A single name is not: "minecraft" on its own is
somebody answering which server they meant.

`Discord:Voice:PrimeWithServerNames` switches it off.

### Added — `/voice status` says which stage is failing

"It didn't hear me" has four causes and from inside a voice channel they are the same silence.
`/voice status` now reports **heard → recognised → addressed → answered** since the bot started, and
names the stage where the numbers stop, so a trigger phrase that is not matching is distinguishable
from a model that is not loaded without switching on transcript logging — which writes down every
private word said in the room to diagnose what is usually one phrase. Counts only; nothing here
holds anything anybody said, which is what makes it safe to leave on.

## [3.19.0] - 2026-08-14

### Added — the bot answers out loud

An answer is spoken into the voice channel as well as posted in its chat. Verified live in a channel
with four people in it: eight requests asked and answered aloud, three to six seconds from the end of
somebody's sentence to the start of the reply.

Synthesis is Kokoro, in this process, **on the GPU** — measured at 40ms for a short reply against
355ms on the processor, for about 700MB of video memory. It asks for CUDA and accepts the processor,
so a host without a usable card answers more slowly rather than not at all, and which one it settled
on is logged because an eightfold difference is otherwise invisible. Unlike recognition, cost scales
with how much is said: a reply that answers the question and stops arrives sooner as well as being
easier to listen to.

The synthesiser is warmed with one throwaway phrase at startup. Loading the model is not the whole
cost of the first call, and measured in a live channel the difference — 1441ms against 340-600ms for
every answer after it — landed entirely on the first person to ask.

`SpokenText` strips what a chat reply carries for the eye: fenced blocks go whole (a stack trace read
aloud is a minute nobody can follow), emphasis and code markers go in place, and the rest is cut to a
sentence under a budget. **What is spoken is always a prefix of what was posted**, never a summary — a
surface that rewords a reply on its way to being read out is one that says things the assistant did
not.

A staged action is spoken as "I've put a confirmation in the chat", and the buttons remain the only
way to approve one. Approving out loud would be a second and weaker gate in front of an irreversible
operation, on the surface where mishearing is routine.

### Changed — the bot joins unmuted, and answering happens off the audio path

A bot that joined muted was one whose replies went nowhere with no error to say so.

Recognised requests now cross a bounded queue to a worker instead of being answered inline. A turn
takes seconds, and all of it used to run inside the tick that closes utterances — one person asking a
question stopped every other speaker's sentence from being finished for as long as it took to answer.
It is also what makes the wiring acyclic: the session owns the connection, so answering out loud goes
back through it, which wired directly is a circle the container refuses at startup.

### Fixed — the deploy carries what synthesis needs

Three things the package would not have delivered on its own. The ONNX Runtime lays its Windows
natives flat beside the Linux ones and P/Invokes the literal name `onnxruntime.dll`, so the resolver
found the Windows file and failed on its ELF header; those are dropped when publishing for a
non-Windows runtime. The same import name never probes `libonnxruntime.so` at all under a single-file
publish, which an ordinary one hides behind the RID asset mapping, so the import is resolved
explicitly. And KokoroSharp copies its voice files on build only — its own targets have no publish
step — leaving a deployed bot with a model and no voice to speak in.

`setup.sh` fetches the synthesis model beside the recognition one, digest-pinned, because the library
otherwise downloads it into the install prefix that every deploy erases.

## [3.18.0] - 2026-08-14

### Added — a spoken request reaches the assistant

What somebody says in a voice channel is put to the kgsm-assistant leaf and answered in that
channel's own chat, which is where the people in it already are. The request is echoed first: a
recogniser mishears, and without seeing what the bot thought it heard an answer about the wrong
server is inexplicable.

**A voice channel is a room**, so everybody in it shares one conversation — the same rule a thread
follows, and for the same reason. Each utterance still carries who said it.

**Speaking is not authority.** The voice connection says which Discord account said something; what
that person may ask this host to do is the KGSM account theirs is connected to, read from the store
every other surface reads. Being in the channel grants nothing, and neither does having invited the
bot. A refusal is said in the channel rather than dropped — from inside a voice call, a bot that
hears you and says nothing is indistinguishable from a broken one.

**A destructive action is still confirmed with a button, deliberately.** There is no spoken "yes":
that would be a second and weaker way to authorise something irreversible, on the one surface where
mishearing is routine. The button re-derives authority at the click and belongs to the person who
asked. `StagedActionPrompt` now holds the wording and the buttons for both surfaces, so a restart
proposed out loud and one proposed in a channel read identically.

### Added — setup.sh provisions the speech model

A host that switched the voice surface on understood nothing until somebody put a 488MB file in
place by hand. `setup.sh` now fetches it into the state directory, after the units are live, so it
writes into the directory systemd already created and needs no privilege.

Pinned by digest, because "the download finished" is not the same claim as "this is the model". A
file that is present and wrong is deleted **before** the re-fetch rather than after it succeeds: if
the download then fails, the bot reports having no model — true and actionable — instead of quietly
recognising nonsense. A failed fetch warns and leaves provisioning to succeed. `KGSM_VOICE_MODEL=0`
skips it on a host that will never listen.

### Fixed — a request cut at the utterance ceiling is no longer half a request

Measured: somebody talking past the twenty-second ceiling had their request cut after the word
"stop", and "minecraft" landed in the following utterance, which carried no trigger and was
discarded. The assistant was asked to stop nothing in particular and could only ask which server.

An utterance cut at the ceiling now says it was cut, and a request read off one is held until that
speaker finishes rather than dispatched. This is the same situation as saying the trigger and
pausing — the speaker has not finished — so both now share one mechanism, per speaker and bounded by
the same window.

## [3.17.0] - 2026-08-14

### Added — the bot understands what it hears

Speech recognition runs in this process, on the GPU where there is one: `ggml-small.en` transcribes
a captured utterance in **107-329ms** measured against a live channel, against 5.1 seconds for the
same model on this host's processor. It asks for CUDA and accepts the CPU, so a host with no card,
or one whose driver is mid-upgrade, gets a slower recogniser rather than no voice surface — and
which one it settled on is logged, because a factor of forty is otherwise invisible.

Cost is per utterance rather than per second: whisper pads what it is given to a fixed thirty-second
window, so a two-second question costs about what a ten-second one does. Utterance length is not a
latency knob.

`WakeWordDetector` decides whether something was addressed to the bot. **The trigger is found
anywhere in what was said** and the request is whatever follows the last occurrence of it. Requiring
it to come first was measured refusing a real request — "okay, let me try this, so hey assistant, is
Ketchup running?" is one breath and therefore one utterance, and an utterance boundary is drawn by
silence, which is a fact about this pipeline rather than about how anybody speaks. The cost is that
somebody quoting the trigger is answered as though they meant it; that is the better failure, and it
is written down as a test rather than left to be discovered.

Saying the trigger alone opens a ten-second window in which that speaker's next utterance is the
request, which is how people address an assistant — attention first, words second.

**What is not addressed to the bot is dropped without being written down.** A voice channel is full
of people talking to each other, and recording their conversation because the bot was in the room is
not something anybody agreed to. `Discord:Voice:LogTranscripts` lifts that for tuning a trigger
phrase, warns loudly at startup while it is on, and is off in the shipped defaults.

### Fixed — sound that is not speech is no longer treated as words

Whisper annotates non-speech rather than returning nothing for it, and three of five recognitions in
a four-person channel were `[BLANK_AUDIO]`. Those reached the matcher as though they were words, and
a follow-up window opened by a bare trigger could be spent by somebody's cough — handing
`[BLANK_AUDIO]` to the assistant as a request. Bracketed annotations are stripped and an utterance
with nothing else in it recognises as nothing.

A quotation mark no longer survives on the end of a request, where it reached the assistant attached
to a server's name.

## [3.16.0] - 2026-08-14

### Added — the bot listens in a voice channel

`/voice join` brings the bot into the channel the caller is already in, `/voice leave` sends it away,
and `/voice status` says where it is and how much it has heard. It joins nowhere on its own: a
microphone that lets itself into a room is a different product from one somebody carried in.

Received audio arrives one stream per speaker, keyed by Discord account and already through DAVE
decryption and the Opus decoder — so who said something is measured rather than worked out, and two
people talking at once are two streams rather than a separation problem. `PcmDownsampler` converts
Discord's 48 kHz stereo to the 16 kHz mono recognition wants, averaging each group of three rather
than picking one of them: dropping samples folds everything above 8 kHz back over the speech band,
which lands as noise exactly where consonants are told apart.

`UtteranceAssembler` decides where one person's speech ends. Frames arrive only while somebody is
talking, so the silences are already in the stream and nothing has to detect them — what is left is
how long a gap has to run before it ends a sentence instead of sitting inside one. A read loop
blocked on the next frame cannot notice the gap it is waiting in, so a ticker alongside it is what
closes an utterance during a conversation; the read loop only ever closes one by hitting the
ceiling.

The commands sit at operator and are not `[Mutating]` — they act on no server at all. What gates
them is that a bot in a voice channel hears everyone present, including people who never addressed
it, which is the same reason `/logs` is there. Joining says so in the channel, naming who asked, and
that message is the only notice anybody else gets.

`Discord:Voice` is **off by default**: every other surface here acts on what somebody typed at it,
and this one takes in the whole room. Nothing is written to disk on any setting — an utterance is
bytes in memory handed on and released.

### Changed — the gateway connects with voice enabled

`GuildVoiceStates` reports who is in which channel, which is how `/voice join` finds the caller and
how an emptied channel is noticed. `EnableVoiceDaveEncryption` is on because Discord answers a client
that cannot negotiate DAVE with close code 4017; it needs `libdave` resolvable at runtime, and
without it voice cannot connect while every other surface is untouched.

## [3.15.1] - 2026-08-14

### Added — a package for libdave, built from Discord's source

`packaging/libdave/PKGBUILD` builds Discord's DAVE implementation and installs it as
`/usr/lib/libdave.so`, on the default `dlopen` search path — so Discord.Net resolves it by name
with nothing copied beside the binary. mlspp and OpenSSL link statically through the pinned vcpkg
submodule, leaving the shared object depending on the toolchain runtime alone.

It compiles upstream rather than repackaging a prebuilt binary. This library sits in the decryption
path of every voice packet, which is not a place to run bytes nobody read. `check()` greps the C ABI
out of the built object, so a build that produced a C++-only library fails at packaging time instead
of inside a voice connection.

The bot lists it under `optdepends`, beside `opus` and `libsodium`: the voice surface cannot open a
connection without all three, and every other surface runs without any of them.

## [3.15.0] - 2026-08-14

### Changed — Discord.Net 3.20.1, which is what a voice connection now requires

Discord enforces the DAVE protocol (its MLS-based end-to-end encryption for audio and video) on
every non-stage voice call, and rejects a client that cannot speak it with voice close code `4017`.
Discord.Net gained `libdave` support in 3.19.0 and spent 3.19.1–3.20.1 fixing the receiving half of
it. The pin moves to 3.20.1 across all four packages, which is the floor for the bot to hold a voice
connection at all.

`Discord.Net.Dave` arrives transitively with `Discord.Net.WebSocket` — there is no separate
reference to add, and the encryption stays off until a client opts in with
`EnableVoiceDaveEncryption`. Using it also needs a `libdave` native library resolvable at runtime,
which nothing here ships yet.

The upgrade costs no source change. The v3.18 component-type break lands on reading
`IMessage.Components` and on modifying an existing message's components, and this bot does neither —
its three `ModifyAsync` calls set `Content` or `Embed`. The modal and select-menu reworks across
3.18–3.20 reach nothing, because the only components here are buttons.

## [3.14.1] - 2026-08-14

### Changed — relicensed from MIT to GPL-3.0-or-later

The whole KGSM ecosystem is GPL-3.0-or-later; this project was the one exception, and its `LICENSE`,
its package metadata and its README all now say so.

### Added — an Arch package, built from the tested binaries

`packaging/PKGBUILD` builds this project into a pacman package. It compiles nothing: CI publishes
first and the recipe places that output, so the packaged bytes are the tested bytes. `pkgver()`
reads `deploy/version.sh`, so the package never restates a version.

The install prefix stays `/opt/<project>` — the same path `deploy.sh` uses — which is what lets the
committed systemd unit ship verbatim instead of being rewritten at packaging time.

Config files are listed in `backup=()`, so an upgrade writes `.pacnew` beside a file you edited
rather than over it. The unit, the sysusers fragment and the leaf descriptor are packaged files, so
the descriptor can never lag the binary it describes. Nothing is enabled by a scriptlet: pacman's
own hooks handle the service account, the state directories and the daemon reload, and enabling a
unit is the administrator's decision.

### Added — one machine-readable version, read rather than restated

`deploy/version.sh` prints this project's version from the single file that declares it, and
`--pkgver` prints the form pacman accepts (a `pkgver` may not contain a hyphen; ordering survives it,
since `vercmp` puts `3.16.0rc3` before `3.16.0`). Packaging asks for a version instead of carrying a
copy that can fall behind the binary.

### Added — the deploy contract is files, not install-time script output

`deploy/polkit/48-kgsm-bot-deploy.rules.in` carries the headless-deploy grant as reviewable content, and
`setup.sh` renders the deploying user and unit list into it instead of embedding the rule in a
heredoc — what a host is granted can now be read without running anything.

`deploy/sysusers.d/kgsm-bot.conf` declares the `kgsm` service account so a packaged install provisions it
declaratively rather than relying on an account that happens to exist.

`deploy/kgsm-bot.requires.json` states every host command, peer service and kernel feature this project
needs — each with its Arch package name, a probe that proves it works, and, for anything optional,
what is lost without it.

### Changed — the committed unit names the service account, not a developer

`User=`/`Group=` read `kgsm`, the account `sysusers.d` declares. `render_unit()` still substitutes
the deploying user at install time, so a dev-host deploy is unchanged.

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
