# Speech: the `kgsm-speech` leaf, reached over a socket

**Hearing and speaking are not in this process — they are the `kgsm-speech` leaf.** One engine per
host, reached over `/run/kgsm-speech/speech.sock`, serving every surface that listens or speaks. The
reason is measured: the models cost about 1.6GB and 1.1GB of video memory, and **unloading them inside
a running process does not give that back** (whisper returned 9MB of 383MB, kokoro 331MB of 1319MB,
after an aggressive compacting GC and `malloc_trim`) — the CUDA runtime behind them is resident until
the process exits. `HostSpeech` owns the connection; `LeafSpeechToText` and `LeafTextToSpeech` are
what the rest of the bot asks.

- **The bot at idle is ~145MB**, and a host that never uses voice never loads a model at all.
- **The engine owns the voice**, not this leaf: every request omits the voice name, which is what
  makes a person hear the same assistant here as anywhere else on the host. `/voice speak-as`
  therefore changes it **for the whole host** and says so. There is deliberately no `SpeechVoice`
  setting here to disagree with it.
- **The bot does not decide how long the models stay loaded** (`ISpeechEngine` has `Wake` and no
  counterpart). A bot leaving a channel is not evidence nobody else is speaking; the engine idles
  out on its own schedule, which is the only place the whole picture is visible.
- **A host without the leaf is the ordinary case**, and it is the same degraded shape as a host with
  no model files: the bot joins, hears nothing, and answers in the channel's chat. This is the one
  place the bot's voice *surface* depends on a sibling leaf — the bot itself does not, exactly as
  slash commands and announcements need no assistant.
- **What stays on this side is what the engine does not know**: this host's server names (priming),
  the echo check, the phrase cache, and the 24kHz→48kHz upsample Discord needs.
- **A scan the speech engine was too busy for is offered again, never queued.**
  `LeafSpeechToText.ScanAsync` answers `VoiceScan.Busy`, the session backs off a tenth of a second and
  offers that speaker again with more audio in the window, so a busy room delays the tone rather than
  losing it, and a scan never stands in front of somebody's request. `/voice status` shows the skips.

The voice surface these serve: `src/KGSM.Bot.Discord/Voice/CLAUDE.md`.
