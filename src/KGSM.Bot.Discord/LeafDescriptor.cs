using TheKrystalShip.KGSM.ComponentConfig;

// What the Control Panel shows about this bot, declared beside the configuration it describes.
// TheKrystalShip.KGSM.ComponentConfig reads this out of the built assemblies and writes
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
[assembly: ConfigSectionAssembly("KGSM.Bot.Infrastructure")]

[assembly: ConfigGroup("general", "General", 1)]
[assembly: ConfigGroup("discord", "Discord", 2)]
[assembly: ConfigGroup("authorization", "Who may act", 3)]
[assembly: ConfigGroup("announcements", "Announcements", 4)]
[assembly: ConfigGroup("channels", "Server channels", 5)]
// The two ambient surfaces: the message kept current in a channel, and the line beside the bot's own
// name. Both say what the host is doing without being asked, and both are paced rather than driven.
[assembly: ConfigGroup("status", "Live status", 6)]
// Beside the status message, because the two answer the same question from opposite ends: that one
// is how a burst becomes a single edit, this one is what paces everything the bot sends regardless.
[assembly: ConfigGroup("limits", "Sending to Discord", 7)]
[assembly: ConfigGroup("connect", "Joining a server", 8)]
[assembly: ConfigGroup("kgsm", "KGSM connection", 9)]
[assembly: ConfigGroup("cache", "Inventory cache", 10)]
[assembly: ConfigGroup("assistant", "Assistant", 11)]
[assembly: ConfigGroup("voice", "Voice", 12)]

// Lowest precedence first — the same order Program.cs registers them in.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-bot/kgsm-bot.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-bot.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-bot/kgsm-bot.env")]

[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]

// ── TheKrystalShip.KGSM.Auth's section, described for this surface ───────────
// The shared Discord application. The type lives in the auth package, which is deliberately free of
// every dependency including this one, so its keys are described here. The values are shared with the
// Control Panel API and the assistant: a host that changes them here without changing them there
// points two surfaces at two different applications.
//
// Who may act is not in this section. It is the KGSM account behind a Discord account, in the store
// declared on AuthOptions over in KGSM.Bot.Infrastructure.

[assembly: ConfigFrameworkField("authClientId", "KgsmAuth__Providers__discord__ClientId", "Discord application id",
    Description = "The Discord application people sign in through on the surfaces that have a sign-in. The bot itself does not, and works without this.",
    Group = "authorization", NoDefault = true)]

[assembly: ConfigFrameworkField("authClientSecret", "KgsmAuth__Providers__discord__ClientSecret", "Discord application secret",
    Description = "Secret for that application. Only the surfaces with a sign-in use it; the bot does not.",
    Group = "authorization", Type = ConfigType.Secret, NoDefault = true)]
