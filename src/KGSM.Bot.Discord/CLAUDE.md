# KGSM.Bot.Discord: the entrypoint and the user surfaces

`Program.cs` builds the host, `BotService` runs the gateway, and the two user surfaces live here:
slash commands (`Commands/`, see `Commands/CLAUDE.md`) and the @-mention surface (`MessageHandler.cs`).
Voice is `Voice/CLAUDE.md`.

## The @-mention surface is a client of the assistant

`MessageHandler` sends the message to the **kgsm-assistant** over its HTTP surface as a
member-to-member call: this bot authenticates as itself with a cluster service token and names the
asking human on `X-Kgsm-Acting` by their `discord:<snowflake>` handle, and the assistant evaluates
what they may do — every tool at the server it touches — against its own replica of the cluster's
authority. **Nothing about access is sent.** The bot runs no model and holds no conversation: the
assistant's tool catalog is what acts, through its own kgsm access. A new assistant capability reaches
Discord with no change here.

`MessageHandler` resolves the author's account first and needs `assistant:chat`, which every active
person holds; someone with no account, a pending one or a disabled one gets the explanation rather
than silence. The surface keys on the channel, so a thread is its own conversation.

The assistant is optional: unconfigured or unreachable, the @-mention surface says so and goes quiet
while slash commands, announcements and channel status carry on. There is deliberately **no fallback
engine** — answering from a second one would split a person's history across two memories exactly
when things are going wrong. A confirmed action reports the assistant's *watched* verdict — "the
engine accepted it" and "the server got there" are different claims.

## What it listens on

Three listeners on one Kestrel host, and what a route answers depends on which one took the call.

| listener | setting | what it serves |
|---|---|---|
| the member wire | `Cluster:Urls` (`http://127.0.0.1:5182`) | the cluster's inbox — what the other members tell this one |
| the status socket | `KGSM:StatusSocketPath` (`/run/kgsm-bot/status.sock`) | `GET /status`: gateway state, a row per configured guild, its channel map, the announcement switches, the send-queue backlog |
| its own surface | `KGSM:SurfaceSocketPath` (`/run/kgsm-bot/surface.sock`) | `/component/*`: this bot's configuration, deploy floors, overrides, journal, unit and command manifest |

**The two sockets answer on the sockets and nowhere else.** Kestrel applies one endpoint map to every
listener, so both groups carry `OwnSocketOnly`, which reads the accepted socket's own local endpoint
and answers no-route for a request that arrived anywhere else. The member wire is a network address
whose callers are other members; what this bot says about itself is bounded by the socket's
filesystem permissions (0660, the node's API in the group), and that is only true while it is
unreachable elsewhere. A request from the wrong listener is a 404 rather than a 403: what is being
stated is that this is not served there, and a 403 would be an admission that it is.

Reading `GET /status` is a genuinely stronger health signal than systemd liveness here — the unit can
be active and the gateway connected while a guild never populated, in which case the bot can post
nothing at all, and the snapshot carries the resolved guild. `BotStatusReporter` builds it per request
off the live client, beside the bot rather than inside it, so it can report a gateway that never
connected.

A change made in the Control Panel is written to `KGSM:ConfigOverridePath`
(`/var/lib/kgsm-api/leaf-overrides/bot.env`), a file this unit already loads with `EnvironmentFile=`.
All of `/component/*` is `TheKrystalShip.KGSM.ComponentSurface`, which lives beside the generator that
writes the descriptor it reads, so this leaf and an anchor answer the same questions the same way.

## `kgsm-bot --adopt-guild-config [--apply]` (`GuildConfigAdoption.cs`)

Imports a single-guild configuration into the guild store. It reads `Discord:GuildId` /
`AnnouncementChannelId` / `InstancesCategoryId` and the `KGSM:Instances` channel map, from a settings
file plus the environment, and writes the store. Dry-run by default; `--from <settings.json>` names the
file to read (default: the settings file beside the binary, which carries no map — point it at the
copy the host was actually running), `--announce-channel <id>` supplies the guild's channel when the
configuration read leaves it at zero. **It refuses a guild that already has a row** rather than
merging — a second run that re-pointed bindings is how live channels get orphaned and duplicated
beside fresh ones, splitting every server's history in two.
