# The guild store

`Guilds:DbPath` — SQLite at `/var/lib/kgsm-bot/bot.db`, `0600`, the bot's own file and its only
writer. It holds which guilds hear about this host (a row per `/setup`), each guild's channel
bindings, board category, follow filter and status-message id.

- **It is deliberately not under `/opt/kgsm-bot`**: `deploy.sh` syncs the prefix with
  `rsync -a --delete`, which would take it every deploy. The directory is the unit's
  `StateDirectory=kgsm-bot`, which systemd creates owned by `User=` before `ExecStart` — so it costs
  no privilege, and the store resolves from `$STATE_DIRECTORY` with the path above as the fallback
  for a bot run outside systemd. Nothing ever overwrites the file.
- **Additive-only**, with a `schema_version` row and a startup floor check that refuses a file a
  newer build wrote — losing this file loses every channel binding, and a binding is the only thing
  tying a server to the channel holding its history.
- **Snowflakes are `TEXT`**: a 64-bit unsigned id in a signed `INTEGER` column is a parse waiting to
  be got wrong.
- **A channel binding is a store row rather than a configuration key** also because of delivery: an
  instance id may contain a hyphen (`minecraft-homestead`), and a systemd environment-variable name
  cannot carry one. A database row has no such constraint.
- **Each guild's follow filter is `guild_servers`, and no rows is no filter.** The board is enabled
  by *having a category* (`board_category_id NOT NULL`), never by a boolean beside one, because a
  flag and a category can disagree.
