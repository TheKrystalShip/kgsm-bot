using TheKrystalShip.KGSM.LeafConfig;

// What the Control Panel shows about this bot, declared beside the configuration it describes.
// TheKrystalShip.KGSM.LeafConfig reads this out of the built assemblies and writes
// deploy/kgsm-bot.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/bot.json, where
// kgsm-api scans for it. The bot itself never reads any of this.
//
// The Discord, KGSM and KgsmCache sections are declared on their own types in KGSM.Bot.Infrastructure.
// Ollama, LlmAgent and Conversation are declared here instead: those types belong to
// TheKrystalShip.Llm, and the bot and the assistant describe the same keys in their own words — "what
// the bot says when it hits the step limit" is not what the assistant says. Prose written for this
// surface has to live with this surface.

[assembly: Leaf(
    id: "bot",
    displayName: "Discord bot",
    unit: "kgsm-bot.service",
    role: "The Discord surface onto KGSM — a channel per server, live status, and authorized commands from chat.")]

// Discord, KGSM and KgsmCache are declared on their own types over there. Named explicitly, so the
// assistant's projects — which this bot also compiles against — cannot leak their sections in here.
[assembly: LeafSectionAssembly("KGSM.Bot.Infrastructure")]

[assembly: LeafGroup("general", "General", 1)]
[assembly: LeafGroup("discord", "Discord", 2)]
[assembly: LeafGroup("announcements", "Announcements", 3)]
[assembly: LeafGroup("channels", "Server channels", 4)]
[assembly: LeafGroup("kgsm", "KGSM connection", 5)]
[assembly: LeafGroup("cache", "Inventory cache", 6)]
[assembly: LeafGroup("model", "Model", 7)]
[assembly: LeafGroup("agent", "Agent loop", 8)]

// Lowest precedence first — the same order Program.cs registers them in.
[assembly: LeafFloorSource("appsettings", "/opt/kgsm-bot/kgsm-bot.settings.json")]
[assembly: LeafFloorSource("systemd-unit", "/etc/kgsm-bot/systemd/kgsm-bot.service")]
[assembly: LeafFloorSource("env-file", "/etc/kgsm-bot/kgsm-bot.env")]

[assembly: LeafFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: LeafFrameworkNamespace("Llm__",
    "prompt overrides are keyed by prompt name, which the bot does not enumerate")]

[assembly: LeafFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = LeafType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]

// ── TheKrystalShip.Llm's sections, described for this surface ────────────────

[assembly: LeafFrameworkField("ollamaEndpoint", "Ollama__Endpoint", "Ollama endpoint",
    Description = "Where the language model is served from. Unreachable, the bot still reports status and runs commands but cannot answer questions.",
    Group = "model", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("ollamaModel", "Ollama__Model", "Model",
    Description = "Name of the model to run, as Ollama knows it. It has to be pulled on the endpoint above and has to support tool calling.",
    Group = "model", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("ollamaNumCtx", "Ollama__NumCtx", "Context window",
    Description = "How many tokens of context the model is given. Larger holds more conversation at once and costs more memory on the machine serving the model.",
    Group = "model", Type = LeafType.Int, Min = 512, Unit = "tokens")]

[assembly: LeafFrameworkField("ollamaTimeoutSec", "Ollama__TimeoutSeconds", "Model timeout",
    Description = "How long to wait for the model to finish a response before giving up on it.",
    Group = "model", Type = LeafType.Int, Min = 1, Unit = "s")]

[assembly: LeafFrameworkField("ollamaTemperature", "Ollama__Temperature", "Temperature",
    Description = "How much the model varies its wording. Low keeps answers consistent and literal, which is what tool calling wants; high makes them more inventive.",
    Group = "model", Type = LeafType.Float, Min = 0, Max = 2)]

[assembly: LeafFrameworkField("ollamaSeed", "Ollama__Seed", "Sampling seed",
    Description = "Fixes the model's randomness so the same question gives the same answer. Leave it unset for normal use.",
    Group = "model", Type = LeafType.Int)]

[assembly: LeafFrameworkField("ollamaThink", "Ollama__Think", "Thinking mode",
    Description = "Asks the model to reason before answering, on models that support it. Slower, and only worth it for a model trained for it.",
    Group = "model", Type = LeafType.Bool)]

[assembly: LeafFrameworkField("conversationDbPath", "Conversation__DatabasePath", "Conversation database",
    Description = "File holding past conversations, which is the bot's memory between messages. Pointing it elsewhere starts an empty history and leaves the old one behind.",
    Group = "agent", Type = LeafType.Path, Risk = LeafRisk.Destructive, NoDefault = true)]

[assembly: LeafFrameworkField("agentMaxIterations", "LlmAgent__MaxIterations", "Maximum tool steps",
    Description = "How many times the bot may call a tool while working on one message before it has to answer with what it has.",
    Group = "agent", Type = LeafType.Int, Min = 1)]

[assembly: LeafFrameworkField("agentMaxToolOutputChars", "LlmAgent__MaxToolOutputChars", "Tool output limit",
    Description = "How much of a tool's output the model is shown. Truncating keeps a long listing from crowding out the conversation.",
    Group = "agent", Type = LeafType.Int, Min = 100, Unit = "chars")]

[assembly: LeafFrameworkField("agentIterationLimitReply", "LlmAgent__IterationLimitReply", "Step-limit reply",
    Description = "What the bot says when it hits the tool-step limit without reaching an answer.",
    Group = "agent")]
