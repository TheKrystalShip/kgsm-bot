# Voice: hear a room, answer out loud

`/voice join|leave|status` at operator (not `[Mutating]` — the gate is on what it exposes, since
everybody in the channel is heard, not only whoever invited it). `Discord:Voice` is off by default.
The path is **hear → scan for the trigger → capture the request → recognise → assistant → speak**,
and the pieces are deliberately separable: capture knows nothing about recognition, and recognition
nothing about who answers. Where the models are (the `kgsm-speech` leaf):
`src/KGSM.Bot.Infrastructure/Speech/CLAUDE.md`.

- **Everything between the connection and a finished request is `TheKrystalShip.Discord.Voice`**
  (published from tks-agent), registered by `AddDiscordVoice`. None of it is about game servers, so
  none of it is in this repo. What is here is the half only this bot can answer: `HostSpeech` and
  `LeafSpeechToText` for where the models are, `SpokenVocabulary` priming for what this host's servers
  are called, and `AssistantVoiceCommandHandler` for what a spoken request means.
  `VoiceOptions.ForVoice()` hands the transport settings over, and the five it does not hand over are
  the ones the answering half reads.
- **DAVE is not optional.** Discord refuses a voice connection from a client that cannot negotiate its
  MLS encryption (close code 4017). `libdave` is packaged in tks-agent, beside the pipeline that needs
  it, and `EnableVoiceDaveEncryption` plus `GuildVoiceStates` are required. Identity and segmentation
  come free — streams are keyed by Discord account id — so there is no diarization and no echo problem.
- **Answering must not run on the audio path.** A turn takes seconds, and run inside the tick that
  closes requests it would freeze every other speaker for the whole time. `VoiceCommandQueue` is the
  handoff, and it is also what makes the wiring acyclic: session → sink → handler → session is a circle
  the container refuses.
- **The trigger is heard mid-conversation, because nobody pauses for it.** Microphones are open and a
  channel keeps talking, so each speaker's last `ScanWindowMs` is scanned for the trigger every
  `ScanStrideMs` of new speech, on kgsm-speech's scan lane: unprimed, never waiting, always behind any
  command read. Finding it plays the listening tone, stops the bot mid-answer, and starts capturing the
  request from the end of the word before the trigger. The request ends after `CommandQuietMs` of quiet
  audio or at `MaxCommandSeconds`, and is then read primed with this host's names. Nothing outside a
  request is ever read as one. **This bot registers no `IVoiceCommandCompleteness`**: requests go to a
  model, which cannot be judged whole early, so they end on quiet.
- **The trigger is matched anywhere in a scan or a request, not at the start.** People lead in
  ("okay let me try — hey assistant, …"). Accepted cost: quoting the phrase fires it.
- **The listening state is two tones, and it is a state rather than a message.** Waiting for you to
  speak and having taken your request are the surface's only two contentless moments, so they are
  marked by `VoiceChimes` — a short notification cut to sound in its first frame to open, a falling
  note to close. A tone costs no synthesis, so it arrives immediately, and it does not wear out the way
  a fixed phrase does. **Tones play through `PlayToneAsync`, which `StopSpeaking` cannot reach**, so a
  trigger never cuts off the tone that answers it. **Anything with something to *tell* you stays
  spoken**: a tone cannot say why, and a rising tone after a confirmation that could not be made out
  reads as "go ahead" when the opposite happened.
- **The tone player is its own seam.** The recogniser plays tones and is a dependency of the session
  that owns the connection, so `IVoiceChimes` resolves the session on first use rather than taking it
  in a constructor — the same circle `VoiceCommandQueue` exists to break. The same tone twice within
  two seconds is played once.
- **The recogniser is primed with this host's names**, because a recogniser knows English and not that
  a server is called `Ketchup`. Nothing downstream rewrites what was said — see the CHANGELOG for why
  correcting a misheard name afterwards is not merely risky but unachievable at any threshold. **How
  the names are written down is the speech package's answer, not this repo's.**
  `TheKrystalShip.Speech.SpokenVocabulary` composes the context and owns `IsEchoOf`, the guard against
  whisper handing that context back as though somebody had read it aloud. What stays here is *reading
  the inventory* and how often — a browser voice note and a spoken request are the same host being
  asked about the same servers, and a second composer here is a second set of names misheard.
- **What is spoken is the whole reply, markup stripped — never a summary and never cut short.** A
  surface that rewords a reply on its way to being read out says things the assistant did not, and
  nothing in the channel would show it happened; one that stops part-way has decided somebody heard
  enough, and leaves the answer in the room disagreeing with the answer in the channel. **How long a
  reply runs is the assistant's to control** — a spoken turn asks for one written to be heard
  (`ReplyStyle.Voice`), and that is the lever, not a cap on this side.
- **The reply is read out sentence by sentence as the assistant writes it.** A voice turn puts its
  question over the assistant's frame stream and hands each `text.delta` to `SpokenSentences`, which
  holds token fragments until there is a whole sentence; `SpokenRecital` synthesises and plays each in
  turn. So the room hears the opening of a long answer while the model is still producing the end of
  it, and what is spoken is unchanged — the whole reply, in order.
  - **Where a reply is cut is the speech package's answer, not this repo's.**
    `TheKrystalShip.Speech.SpokenSentences` holds the rules — a fenced block dropped rather than read
    line by line, a sentence ending only at punctuation *followed by whitespace* so `kgsm.sh` and
    `2.0.58` survive, a short sentence riding out with the next, and whatever is left said by the
    flush. `SpokenSentences.Whole` applies the same rules to a string this surface writes itself. Every
    surface on the host that speaks reads them from there, because two surfaces answering that
    question separately answer it differently within a release. **Do not add a second stripper or a
    second segmenter here.**
  - **Streaming is what the turn is watched for, not a setting.** `Voice:Speak` off, or no synthesiser
    on the host, means no recital — and with no recital the turn takes the buffered reply exactly as a
    typed question does.
  - **Everything spoken for a turn rides one recital**, including the sentence pointing at a staged
    action's buttons and the sentence said when a turn fails. Anything said beside it would survive an
    interruption and talk over whoever cut in.
- **The trigger cuts the bot off, and nothing weaker does.** Saying it while an answer is playing stops
  that answer (`IVoiceSessions.StopSpeaking`, `Voice:Interruptible`), and the request that stopped it
  is answered next. **The whole answer goes, not the sentence in the air**: an answer read out as it is
  written is a queue of sentences, so the moment somebody cuts in is as likely to fall in the gap
  between two of them as inside one — and dropping only what is playing has the bot pause and then
  resume over the top of them, which is worse than not letting them cut in at all. The session
  therefore abandons the current recital as well, whether or not anything was playing, and every piece
  still owed to it is refused. A piece is checked twice, and **the check that matters is the one after
  the wait** — a sentence queued behind another spends that sentence's whole duration waiting for the
  mouth, which is exactly the window an interruption lands in. Receiving never pauses for speaking —
  they are separate directions on one connection — so the cheap signal, *somebody started talking*, is
  already in the pipe at ~20ms against the ~1.5s a trigger match costs. It is deliberately not used: a
  voice channel is a room where people talk over each other, and acting on it silences the bot every
  time two of them do. Continuing a conversation is not cutting into one, so an answer inside a
  `VoiceAttention` window and the rest of a request truncated at the ceiling both leave the speech
  alone.
- **Stopping the write does not stop the sound — the stream has to go.** Up to `BufferMillis` of audio
  is already queued in the writer and plays on regardless, so an interrupt disposes `_out` and the next
  answer builds a new one. The writer's own `ClearAsync` **cannot** be used: it dequeues frames without
  releasing the queue slots or returning their buffers to the pool, so one call starves the stream for
  good. Every path that abandons the stream disposes it, because a `BufferedWriteStream` owns a send
  loop that runs until something cancels it — a dropped reference is a second writer on the same
  connection as its replacement.
- **Clearing or compacting a conversation is read BEFORE a turn exists.** `/conversation clear` and
  `/conversation compact`, and spoken ("start over", "forget everything") via
  `SpokenConversationCommands` → `IAssistantTurnClient.RunCommandAsync` → the assistant's own
  `/commands/{name}`. These act on the stored conversation, so they can never be a question: a model
  told to forget replies that it has and remembers every word. The phrase list is matched
  deterministically against the **whole request** — containment would turn *"the server didn't start
  over the weekend"* into a wipe — and the assistant owns what each command does and who may run it,
  including the operator gate on clearing a shared room. Its wording is shown and spoken verbatim;
  nothing here forms a second opinion about what happened.
- **A staged action is never approved out loud.** It is offered with the same buttons the @-mention
  surface posts and the spoken reply says so. The button re-derives authority at the click; a
  recogniser cannot, and a spoken yes would be a second way to authorise a destructive action.
- **Audio shorter than the output buffer is padded to it, and every write is bounded.** Discord.Net's
  buffered writer transmits nothing until its queue holds a full buffer's worth of frames — below that
  its send loop waits forever and the flush waits on the send loop, so a short write neither returns
  nor throws. Measured: one 290ms tone wedged the stream and, because requests are answered one at a
  time, silenced the whole surface with nothing in the log. `SendableAudio` owns the floor and the
  buffer length together, since the two disagreeing is the cause. A short **spoken** answer ("Yes.")
  hits this too — it is a property of the writer, not of tones.
- **Speaking is best-effort throughout.** No model, no card, or a broken output stream costs the audio
  and nothing else — the answer is already in the channel.
- **Nothing is written to disk.** Audio is bytes in memory, held per speaker only as long as a request
  could still need it. This is a bot that hears a room, not one that records it, and the difference is
  structural rather than a setting. `LogTranscripts` is the one exception, opt-in, and warns while it
  is on: requests at information, everything scanned at debug.
