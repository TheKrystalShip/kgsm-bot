# Authorization: the KGSM account behind a Discord user

**A Discord account says who you are; the KGSM account it is connected to says what you may do.** The
bot has no login of its own, so the Discord user the gateway names *is* the identity — and the tier is
whatever KGSM account that identity is a credential of, read from the host's account store
(`/var/lib/kgsm/auth/users.db`, `Auth:UsersDbPath`, `TheKrystalShip.KGSM.Auth.Users`). The Control
Panel and the assistant read the same record, so all three agree by construction rather than by each
deriving an answer.

**A guild role grants nothing, and neither does guild membership.** The gate is having an account
here, which an admin granted — strictly narrower than being in the Discord server, and the reason the
slash commands are safe registered globally. `/etc/kgsm/kgsm-auth.env` carries the sign-in
application and nothing else.

**In a cluster the accounts are a level-1 replica, given to this bot by the auth anchor.** The anchor is
the single authority; every member holds its own copy, pushed to it, and answers from that. This bot
therefore identifies a Discord user itself and never asks another member — the assistant it forwards a
turn to resolves the same person against *its* copy of the same source, and neither takes the other's
word for who somebody is. A copy of a copy would put a second member's freshness between a person and
what they may do here.

Receiving that copy is what the member wire is for: `Cluster:Urls` is the address this bot listens on,
`Cluster:GossipUrl` is the address the other members reach it at, and the anchor fans each account
change into that. **Both are needed.** A member that binds but states no address is learned by the mesh
with no address at all — a loopback bind is never advertised, because a loopback address means "me" to
whoever reads it — and is then never pushed to. A member with nowhere to be reached would hold
whatever it copied on joining and never hear that somebody was demoted. A machine standing alone has no
anchor and no replica — it reads the accounts on its own host.

**A member holding no copy yet refuses everybody, and says so.** Until the first snapshot lands there is
nothing to identify anyone against, and an empty store answers a real person and a stranger identically —
so it is reported as *the accounts could not be read*, never as *you have no account*. That is the same
distinction `Unreadable` draws, applied to the one state only a member can be in.

The store is opened **directly off the file**, not asked for over HTTP: a file cannot be down, so the
bot keeps authorizing people with every other leaf stopped. Reads are **uncached** — a point query
against a local file, at Discord typing speed — so an admin changing somebody's tier in the panel
lands on their very next command with no window at the old one.

`IKgsmAccounts.ResolveAsync` gives **four** answers, not a tier, and `AccountAnswer.Refusal` writes
the whole sentence for each so every surface refuses somebody in the same words:

| outcome | means | told |
|---|---|---|
| `Ok` | an account, usable; its tier is on the answer | — (or which tier it lacks) |
| `NotLinked` | no KGSM account has this Discord account connected | how to connect it |
| `Disabled` | the account was switched off | that it is disabled |
| `Unreadable` | the store could not be read | that nothing is known, and nothing was done |

`Unreadable` is deliberately not a denial. *"We could not ask"* is a different fact from *"the answer
is no"*, and reporting the first as the second demotes an admin mid-incident.

How each surface applies it (slash preconditions, the @-mention surface, confirm buttons):
`src/KGSM.Bot.Discord/Commands/CLAUDE.md`.
