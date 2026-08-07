using TheKrystalShip.KGSM.LeafConfig;

// What the Control Panel shows about this bot, declared beside the configuration it describes.
// TheKrystalShip.KGSM.LeafConfig reads this out of the built assemblies and writes
// deploy/kgsm-bot.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/bot.json, where
// kgsm-api scans for it. The bot itself never reads any of this.
//
// The Discord, KGSM, KgsmCache and Assistant sections are declared on their own types in
// KGSM.Bot.Infrastructure. KgsmAuth is declared here instead: that type belongs to the shared auth
// package, and each surface describes the same keys in its own words, so the prose has to live with
// the surface that shows it.

[assembly: Leaf(
    id: "bot",
    displayName: "Discord bot",
    unit: "kgsm-bot.service",
    role: "The Discord surface onto KGSM — a channel per server, live status, and authorized commands from chat.")]

// The bound settings types live over there. Named explicitly, so a referenced package's own settings
// types cannot leak their sections in here.
[assembly: LeafSectionAssembly("KGSM.Bot.Infrastructure")]

[assembly: LeafGroup("general", "General", 1)]
[assembly: LeafGroup("discord", "Discord", 2)]
[assembly: LeafGroup("authorization", "Who may act", 3)]
[assembly: LeafGroup("announcements", "Announcements", 4)]
[assembly: LeafGroup("channels", "Server channels", 5)]
[assembly: LeafGroup("kgsm", "KGSM connection", 6)]
[assembly: LeafGroup("cache", "Inventory cache", 7)]
[assembly: LeafGroup("assistant", "Assistant", 8)]

// Lowest precedence first — the same order Program.cs registers them in.
[assembly: LeafFloorSource("appsettings", "/opt/kgsm-bot/kgsm-bot.settings.json")]
[assembly: LeafFloorSource("systemd-unit", "/etc/kgsm-bot/systemd/kgsm-bot.service")]
[assembly: LeafFloorSource("env-file", "/etc/kgsm-bot/kgsm-bot.env")]

[assembly: LeafFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: LeafFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = LeafType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]

// ── TheKrystalShip.KGSM.Auth's section, described for this surface ───────────
// The shared authorization block. The type lives in the auth package, which is deliberately free of
// every dependency including this one, so its keys are described here. The values are shared with the
// Control Panel API and the assistant: a host that changes them here without changing them there
// gives one person two different answers.

[assembly: LeafFrameworkField("authGuildId", "KgsmAuth__GuildId", "Discord server id",
    Description = "The Discord server whose membership and roles decide who may do what. Someone who is not in it is refused outright.",
    Group = "authorization", Risk = LeafRisk.Wiring, NoDefault = true)]

[assembly: LeafFrameworkField("authBotToken", "KgsmAuth__BotToken", "Role lookup token",
    Description = "Token used to read which roles a person holds. Sign-in itself never asks Discord for roles, so without this nobody can be elevated above read-only.",
    Group = "authorization", Type = LeafType.Secret, Risk = LeafRisk.Wiring, NoDefault = true)]

[assembly: LeafFrameworkField("authClientId", "KgsmAuth__ClientId", "Discord application id",
    Description = "The Discord application people sign in through on the surfaces that have a sign-in. The bot itself does not, and works without this.",
    Group = "authorization", NoDefault = true)]

[assembly: LeafFrameworkField("authClientSecret", "KgsmAuth__ClientSecret", "Discord application secret",
    Description = "Secret for that application. Only the surfaces with a sign-in use it; the bot does not.",
    Group = "authorization", Type = LeafType.Secret, NoDefault = true)]

[assembly: LeafFrameworkField("authRoleAdminIds", "KgsmAuth__RoleAdminIds", "Admin roles",
    Description = "Comma-separated role ids granting admin — host settings, and reading other people\u0027s conversations. Admin includes everything an operator may do.",
    Group = "authorization", Risk = LeafRisk.Wiring, NoDefault = true)]

[assembly: LeafFrameworkField("authRoleOperatorIds", "KgsmAuth__RoleOperatorIds", "Operator roles",
    Description = "Comma-separated role ids granting operator — starting, stopping, installing and uninstalling servers. Empty means nobody can act, from any surface.",
    Group = "authorization", Risk = LeafRisk.Wiring, NoDefault = true)]
