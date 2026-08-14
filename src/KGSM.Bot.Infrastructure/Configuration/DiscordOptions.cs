using KGSM.Bot.Core.Models;

using TheKrystalShip.KGSM.LeafConfig;

namespace KGSM.Bot.Infrastructure.Configuration;

/// <summary>
/// Configuration options for Discord — this host's own policy, not any one Discord server's.
/// </summary>
/// <remarks>
/// Where announcements land is not here: which guilds hear about this host, the channel each of them
/// takes announcements in, and whether it keeps a channel per server are all set with <c>/setup</c>
/// from inside Discord and held in the guild store. What this host announces at all, and how a
/// message looks when it does, is host policy and lives here.
/// </remarks>
[LeafSection(Section)]
public class DiscordOptions
{
    public const string Section = "Discord";

    /// <panel>Token the bot signs in to Discord with. Without a valid one it cannot connect at
    /// all.</panel>
    [LeafField("discordToken", "Bot token", Group = "discord", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string Token { get; set; } = string.Empty;

    /// <panel>Whether uninstalling a server also deletes its Discord channel, taking that channel's
    /// history with it. Off, the channel is left behind.</panel>
    [LeafField("removeChannelOnUninstall", "Delete channel with the server", Group = "channels",
        Risk = LeafRisk.Destructive)]
    public bool RemoveChannelOnInstanceDeletion { get; set; } = false;

    /// <summary>
    /// The address the bot tells people to connect to. Set it when this host is reached by a name
    /// rather than by its IP.
    /// </summary>
    /// <remarks>
    /// A host cannot discover this: a DNS record pointing at it is a fact about the world, not about
    /// the machine. So an operator-set value is authoritative and is used verbatim; left blank, the
    /// bot falls back to the external IP the host measured, which is correct at the moment it is read
    /// and is not a promise.
    /// </remarks>
    /// <panel>The address the bot tells people to connect to — a domain name, if this host has one.
    /// Left blank, it uses the external IP address the host measures for itself.</panel>
    [LeafField("publicAddress", "Public address", Group = "connect")]
    public string PublicAddress { get; set; } = string.Empty;

    /// <summary>
    /// Whether an announcement about a server that is down carries a button to restart it.
    /// </summary>
    /// <remarks>
    /// The button is a shortcut to the slash command and nothing more: it is authorized at the click
    /// against the same account store, runs the same path, and stamps the same provenance. Turning it
    /// off costs the shortcut, never the safety — nobody gains authority from a button.
    /// </remarks>
    /// <panel>Whether an announcement about a server that is down carries a button to restart it.
    /// Pressing it needs the same permission the command does.</panel>
    [LeafField("announcementActions", "Buttons on announcements", Group = "announcements")]
    public bool ActionButtons { get; set; } = true;

    /// <summary>
    /// Whether a crash opens a thread under its announcement for the conversation about it.
    /// </summary>
    /// <remarks>
    /// Needs <c>Create Public Threads</c> in the channel; without it the announcement is posted
    /// plainly and nothing is lost. Threads auto-archive, so nothing accumulates.
    /// </remarks>
    /// <panel>Whether a crash opens a thread under its announcement, so the conversation about it
    /// stays with it instead of scrolling the channel. Needs permission to create threads.</panel>
    [LeafField("incidentThreads", "Threads for crashes", Group = "announcements")]
    public bool IncidentThreads { get; set; } = true;

    /// <summary>
    /// Whether the assistant investigates a server the supervisor has given up on, and posts what it
    /// found in the thread opened for it.
    /// </summary>
    /// <remarks>
    /// Needs an assistant configured on this host and a thread to post in; without either, a give-up
    /// is announced exactly as it is with this off. The investigation only ever reads — it is asked
    /// with no auto-run, and anything it proposes is dropped rather than offered to a thread that
    /// asked for nothing.
    /// </remarks>
    /// <panel>Whether the assistant looks into a server the supervisor has given up on and posts what
    /// it found in the crash thread, before anybody asks. It only reads; it never acts on its own.</panel>
    [LeafField("incidentTriage", "Investigate crashes", Group = "announcements")]
    public bool IncidentTriage { get; set; } = true;

    /// <summary>
    /// The floor on how often a guild's live status message is edited.
    /// </summary>
    /// <remarks>
    /// This is what turns a burst into one edit. A host reboot produces one event per server in the
    /// same second, and spending an edit on each is how a bot gets throttled off the API — losing the
    /// announcements with it. Everything that arrives inside the window is published together.
    /// </remarks>
    /// <panel>How long the bot waits before editing the live status message again, so a burst of
    /// changes becomes one edit instead of one each.</panel>
    [LeafField("statusMessageMinIntervalSec", "Status message floor", Group = "status",
        Min = 5, Unit = "s")]
    public int StatusMessageMinIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How often the live status message is republished even when nothing changed.
    /// </summary>
    /// <remarks>
    /// The backstop, not the mechanism: the message is driven by events, and this only catches what
    /// no event describes — a server stopped outside the engine, a missed journal line, a message
    /// somebody deleted.
    /// </remarks>
    /// <panel>How often the live status message is refreshed even when nothing has happened, to catch
    /// anything no event reported.</panel>
    [LeafField("statusMessageRefreshSec", "Status message refresh", Group = "status",
        Min = 60, Unit = "s")]
    public int StatusMessageRefreshSeconds { get; set; } = 900;

    /// <summary>
    /// How old a server's newest backup may be before the live status message says so.
    /// </summary>
    /// <remarks>
    /// <b>The board stays quiet while backups are current and speaks when they are not.</b> Printing
    /// an age beside all sixteen servers would make the one that matters invisible among fifteen that
    /// do not — so the marker appears only past this age, and for a server with no backup at all.
    /// Zero shows it for every server, for an operator who would rather see the number always.
    /// </remarks>
    /// <panel>How old a server's newest backup has to be before the live status message flags it. A
    /// server with no backup at all is always flagged. Set to 0 to show the age for every server.</panel>
    [LeafField("backupStaleAfterHours", "Flag backups older than", Group = "status",
        Min = 0, Unit = "h")]
    public int BackupStaleAfterHours { get; set; } = 48;

    /// <summary>
    /// Whether the read commands answer only the person who ran them.
    /// </summary>
    /// <remarks>
    /// <c>/status</c>, <c>/list</c>, <c>/is-active</c> and <c>/supervision</c> are one person's
    /// question, and a busy channel does not need everyone's status checks in its scrollback. The
    /// commands whose whole point is to be shared — <c>/connect</c> above all — are deliberately not
    /// covered by this, and <c>/logs</c> is always private whatever this says.
    /// </remarks>
    /// <panel>Whether answers to "what's the status" commands are shown only to the person who asked,
    /// instead of to the whole channel. Sharing commands like /connect are unaffected.</panel>
    [LeafField("ephemeralReads", "Keep status answers private", Group = "channels")]
    public bool EphemeralReads { get; set; } = true;

    /// <summary>
    /// Whether the bot shows this host in its own Discord presence.
    /// </summary>
    /// <remarks>
    /// The one thing this bot says without a channel to say it in: it reaches a guild that has never
    /// run <c>/setup</c>, and one update covers every guild at once. Off, the bot appears with no
    /// activity and nothing else changes.
    /// </remarks>
    /// <panel>Whether the bot shows how many servers are up beside its name in the member list. It
    /// needs no channel and no setup, and every Discord server sees the same line.</panel>
    [LeafField("presence", "Show the host in the member list", Group = "status")]
    public bool Presence { get; set; } = true;

    /// <summary>
    /// How often the presence is recomposed.
    /// </summary>
    /// <remarks>
    /// A gateway presence update is limited to a handful per twenty seconds for the whole session, and
    /// that budget is not the one the send queue paces. This cadence is the only thing protecting it,
    /// which is why the presence is never driven by an event: the line is recomposed on the tick and
    /// sent only when it changed.
    /// </remarks>
    /// <panel>How often the bot re-checks the host to update the line beside its name. Set it low and
    /// Discord will start refusing the updates.</panel>
    [LeafField("presenceRefreshSec", "Presence refresh", Group = "status",
        Min = 20, Unit = "s", DependsOn = "presence")]
    public int PresenceRefreshSeconds { get; set; } = 60;

    /// <summary>
    /// The floor between two calls the bot makes to Discord on its own initiative.
    /// </summary>
    /// <remarks>
    /// This is what keeps a limit from being reached at all, and it is worth more than any backoff —
    /// a 429 has already spent the request that earned it. Announcements fan out across guilds, the
    /// board edits a message per guild, and installs create channels; without a floor those are
    /// several producers bursting at once with no knowledge of each other. An idle bot pays nothing:
    /// the floor is a gap between calls, not a delay on every one.
    /// </remarks>
    /// <panel>How long the bot waits between two messages it sends on its own, so a burst is spread
    /// out instead of arriving at once and getting the bot throttled.</panel>
    [LeafField("sendQueueMinIntervalMs", "Send floor", Group = "limits",
        Min = 0, Max = 5000, Unit = "ms", Risk = LeafRisk.Wiring)]
    public int SendQueueMinIntervalMs { get; set; } = 200;

    /// <summary>
    /// How many calls may wait in each of the two lanes before further ones are refused.
    /// </summary>
    /// <remarks>
    /// An unbounded queue in front of a rate limit is a memory leak with a delay on it. Overflow is
    /// refused and reported, never dropped quietly: a caller's own accounting shows the guild it did
    /// not reach, because a bot announcing nothing must not look like a host where nothing happened.
    /// </remarks>
    /// <panel>How many messages may wait to be sent before the bot starts refusing new ones and
    /// saying so. Reaching this means Discord is not keeping up with this host.</panel>
    [LeafField("sendQueueCapacity", "Send queue size", Group = "limits",
        Min = 16, Risk = LeafRisk.Wiring)]
    public int SendQueueCapacity { get; set; } = 500;

    /// <summary>
    /// How many times one call is attempted before it is given up on.
    /// </summary>
    /// <remarks>
    /// Only a rate limit, a server error or a dropped connection is retried at all. A refusal, a
    /// missing channel or a malformed request is the answer rather than a hiccup, and is failed on
    /// the first attempt however high this is set.
    /// </remarks>
    /// <panel>How many times the bot re-tries a message Discord could not take, before giving up on
    /// it. Only a temporary failure is re-tried.</panel>
    [LeafField("sendQueueMaxAttempts", "Send attempts", Group = "limits",
        Min = 1, Max = 10, Risk = LeafRisk.Wiring)]
    public int SendQueueMaxAttempts { get; set; } = 4;

    /// <summary>The first hold-off after a rate limit or a server error; it doubles from here.</summary>
    /// <panel>How long the bot pauses everything it is sending after Discord refuses a message for a
    /// temporary reason. It doubles each time until the ceiling below.</panel>
    [LeafField("sendQueueBackoffMs", "Backoff", Group = "limits",
        Min = 100, Unit = "ms", Risk = LeafRisk.Wiring)]
    public int SendQueueBackoffMs { get; set; } = 1000;

    /// <summary>The ceiling the doubling hold-off stops at.</summary>
    /// <panel>The longest the bot will pause between re-tries, however many times sending has
    /// failed.</panel>
    [LeafField("sendQueueMaxBackoffMs", "Backoff ceiling", Group = "limits",
        Min = 1000, Unit = "ms", Risk = LeafRisk.Wiring)]
    public int SendQueueMaxBackoffMs { get; set; } = 60000;

    public StatusOptions Status { get; set; } = new();
    public AnnouncementOptions Announce { get; set; } = new();
    public VoiceOptions Voice { get; set; } = new();
    /// <panel>Whether announcements are deleted again after a while, so a busy channel does not fill
    /// with them. On, the channel keeps no record of what happened.</panel>
    [LeafField("deleteStatusMessages", "Clean up announcements", Group = "channels")]
    public bool DeleteStatusMessageAfterDelay { get; set; } = false;
    /// <panel>How long an announcement stays before it is deleted.</panel>
    [LeafField("deleteStatusMessagesAfterSec", "Announcement lifetime", Group = "channels",
        Min = 1, Unit = "s", DependsOn = "deleteStatusMessages")]
    public int DeleteStatusMessageDelaySeconds { get; set; } = 300;
}

/// <summary>
/// Which events the bot announces. One switch per <see cref="Core.Models.AnnouncementKind"/>, so
/// the operator turns off the noise their guild does not want without losing the rest.
/// </summary>
/// <remarks>
/// The defaults are the events an operator would be surprised to miss — a server going up or down,
/// a crash, an update, an install. Everything that fires on its own cadence rather than on an
/// operator's action starts off: a busy server produces a join and a leave per player per session,
/// and <c>instance_ready</c> follows every start (including the supervisor's own restarts), so both
/// would double the traffic in a channel for a fact the operator can already see.
/// </remarks>
public class AnnouncementOptions
{
    /// <panel>A server's process was launched.</panel>
    [LeafField("announceStarted", "Server started", Group = "announcements")]
    public bool Started { get; set; } = true;

    /// <panel>A server finished loading and is playable. Follows every start, including the ones the
    /// supervisor makes on its own, so leaving it off keeps a channel to one message per start.</panel>
    [LeafField("announceReady", "Server ready to play", Group = "announcements")]
    public bool Ready { get; set; } = false;

    /// <panel>A server was stopped.</panel>
    [LeafField("announceStopped", "Server stopped", Group = "announcements")]
    public bool Stopped { get; set; } = true;

    /// <panel>Someone cycled a server deliberately.</panel>
    [LeafField("announceRestarted", "Server restarted", Group = "announcements")]
    public bool Restarted { get; set; } = true;

    /// <panel>A server died unexpectedly and the supervisor is restarting it. Announced once per
    /// crash, not once per restart attempt.</panel>
    [LeafField("announceCrashed", "Server crashed", Group = "announcements")]
    public bool Crashed { get; set; } = true;

    /// <panel>The supervisor ran out of restart attempts and left a server down. This is the one
    /// that means somebody has to go look.</panel>
    [LeafField("announceFailed", "Server gave up restarting", Group = "announcements")]
    public bool Failed { get; set; } = true;

    /// <panel>A newer game build was released for a server. Announced once per build, not once per
    /// check, and only for servers this host actually checks.</panel>
    [LeafField("announceUpdateAvailable", "Game update available", Group = "announcements")]
    public bool UpdateAvailable { get; set; } = true;

    /// <panel>A new game build was applied to a server.</panel>
    [LeafField("announceUpdated", "Game updated", Group = "announcements")]
    public bool Updated { get; set; } = true;

    /// <panel>A new server was installed.</panel>
    [LeafField("announceInstalled", "Server installed", Group = "announcements")]
    public bool Installed { get; set; } = true;

    /// <panel>A server was uninstalled.</panel>
    [LeafField("announceUninstalled", "Server uninstalled", Group = "announcements")]
    public bool Uninstalled { get; set; } = true;

    /// <panel>A backup of a server was written.</panel>
    [LeafField("announceBackupCreated", "Backup created", Group = "announcements")]
    public bool BackupCreated { get; set; } = false;

    /// <panel>A backup was rolled back onto a server, replacing what was there.</panel>
    [LeafField("announceBackupRestored", "Backup restored", Group = "announcements")]
    public bool BackupRestored { get; set; } = true;

    /// <panel>A player connected. One message per join — busy on a popular server.</panel>
    [LeafField("announcePlayerJoined", "Player joined", Group = "announcements")]
    public bool PlayerJoined { get; set; } = false;

    /// <panel>A player disconnected. One message per leave.</panel>
    [LeafField("announcePlayerLeft", "Player left", Group = "announcements")]
    public bool PlayerLeft { get; set; } = false;

    /// <panel>A player was kicked, banned, or had a ban lifted.</panel>
    [LeafField("announceModeration", "Player kicked or banned", Group = "announcements")]
    public bool Moderation { get; set; } = true;

    /// <summary>
    /// Whether this kind of announcement is switched on. The three moderation kinds share one
    /// switch: an operator who wants to hear about bans wants to hear about kicks.
    /// </summary>
    /// <remarks>
    /// A kind with no case here is a kind somebody added without deciding whether an operator may
    /// turn it off. Announcing it is the safer half of that mistake — a message nobody asked for is
    /// visible and gets fixed, silence is not — and the test over this method is what stops it
    /// reaching a channel in the first place.
    /// </remarks>
    public bool IsEnabled(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Started => Started,
        AnnouncementKind.Ready => Ready,
        AnnouncementKind.Stopped => Stopped,
        AnnouncementKind.Restarted => Restarted,
        AnnouncementKind.Crashed => Crashed,
        AnnouncementKind.Failed => Failed,
        AnnouncementKind.UpdateAvailable => UpdateAvailable,
        AnnouncementKind.Updated => Updated,
        AnnouncementKind.Installed => Installed,
        AnnouncementKind.Uninstalled => Uninstalled,
        AnnouncementKind.BackupCreated => BackupCreated,
        AnnouncementKind.BackupRestored => BackupRestored,
        AnnouncementKind.PlayerJoined => PlayerJoined,
        AnnouncementKind.PlayerLeft => PlayerLeft,
        AnnouncementKind.PlayerKicked => Moderation,
        AnnouncementKind.PlayerBanned => Moderation,
        AnnouncementKind.PlayerUnbanned => Moderation,
        _ => true,
    };
}

/// <summary>
/// Configuration options for status messages
/// </summary>
public class StatusOptions
{
    /// <panel>Shown beside a server that is running.</panel>
    [LeafField("statusOnline", "Running marker", Group = "channels")]
    public string Online { get; set; } = string.Empty;

    /// <panel>Shown beside a server that is stopped.</panel>
    [LeafField("statusOffline", "Stopped marker", Group = "channels")]
    public string Offline { get; set; } = string.Empty;

    /// <panel>Shown beside a server that is no longer installed.</panel>
    [LeafField("statusUninstalled", "Uninstalled marker", Group = "channels")]
    public string Uninstalled { get; set; } = string.Empty;
}

/// <summary>
/// The voice surface: whether the bot may sit in a voice channel and listen, and how it decides
/// where one person's speech ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default, and deliberately.</b> Every other surface here acts on what somebody typed at
/// it; this one takes in everybody in the room, including people who never addressed the bot and
/// cannot see that it is listening. That is an operator's decision to make on purpose, not one to
/// inherit from a default.
/// </para>
/// <para>
/// Nothing is written to disk and nothing is kept: an utterance exists as bytes in memory for as
/// long as it takes to hand it on, and no configuration here can turn that into recording.
/// </para>
/// </remarks>
public class VoiceOptions
{
    /// <panel>Whether the bot may join a voice channel and listen to what is said in it. Off, the
    /// voice commands refuse and no audio is ever received. Everyone in a channel the bot is in is
    /// heard, not only whoever invited it.</panel>
    [LeafField("voiceEnabled", "Voice listening", Group = "voice", Risk = LeafRisk.Wiring)]
    public bool Enabled { get; set; } = false;

    /// <panel>How long somebody has to stop talking before the bot treats their sentence as
    /// finished. Too short cuts people off mid-sentence; too long makes every answer wait.</panel>
    [LeafField("voiceSilenceGapMs", "End-of-speech silence", Group = "voice",
        Min = 100, Max = 5000, Unit = "ms", DependsOn = "voiceEnabled")]
    public int SilenceGapMs { get; set; } = 800;

    /// <panel>The shortest sound worth passing on. Below this it is a cough or a keyboard, not
    /// speech.</panel>
    [LeafField("voiceMinUtteranceMs", "Shortest utterance", Group = "voice",
        Min = 100, Max = 5000, Unit = "ms", DependsOn = "voiceEnabled")]
    public int MinUtteranceMs { get; set; } = 400;

    /// <panel>The longest one person may talk before the bot cuts it and takes what it has. Somebody
    /// talking without pausing is normal; an unbounded buffer is not.</panel>
    [LeafField("voiceMaxUtteranceSeconds", "Longest utterance", Group = "voice",
        Min = 1, Max = 120, Unit = "s", DependsOn = "voiceEnabled")]
    public int MaxUtteranceSeconds { get; set; } = 20;

    /// <panel>Whether the bot leaves once it is the only one left in the channel. Off, it stays until
    /// somebody tells it to leave.</panel>
    [LeafField("voiceLeaveWhenAlone", "Leave an empty channel", Group = "voice",
        DependsOn = "voiceEnabled")]
    public bool LeaveWhenAlone { get; set; } = true;

    /// <summary>
    /// The whisper model used to recognise speech, as a path to a <c>ggml-*.bin</c> file.
    /// </summary>
    /// <remarks>
    /// Not under the install prefix: <c>deploy.sh</c> syncs that with <c>rsync --delete</c> and would
    /// take a model file with it on every deploy. The state directory is the bot's own and survives.
    /// </remarks>
    /// <panel>The speech recognition model file. <code>ggml-small.en.bin</code> is the one to want —
    /// on a GPU it is both more accurate and faster than the smaller models are on a CPU. Without a
    /// model nothing said in a voice channel is understood.</panel>
    [LeafField("voiceModelPath", "Speech model", Group = "voice", Type = LeafType.Path,
        DependsOn = "voiceEnabled")]
    public string ModelPath { get; set; } = "/var/lib/kgsm-bot/models/ggml-small.en.bin";

    /// <panel>Whether to recognise speech on the graphics card. A host with no usable GPU falls back
    /// to the processor on its own — which works, and is around forty times slower.</panel>
    [LeafField("voiceUseGpu", "Recognise on the GPU", Group = "voice", DependsOn = "voiceEnabled")]
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// What somebody says to address the bot. Several, comma-separated, all equal.
    /// </summary>
    /// <remarks>
    /// A list because this is matched against a transcript rather than against sound: whichever way
    /// the recogniser renders the phrase is what has to be matched, and an operator who sees a
    /// variant in the log adds it here with nothing to retrain.
    /// </remarks>
    /// <panel>What somebody says to get the bot's attention, like <code>hey assistant</code>. It is
    /// found anywhere in a sentence, so leading into a request works — and so does quoting the
    /// phrase, which will get you an answer. Several may be given, separated by commas.</panel>
    [LeafField("voiceTriggers", "Trigger phrase", Group = "voice", DependsOn = "voiceEnabled")]
    public string Triggers { get; set; } = "hey assistant";

    /// <summary>
    /// Whether to tell the recogniser this host's server names before asking it to recognise anything.
    /// </summary>
    /// <remarks>
    /// A knob rather than a certainty because conditioning a recogniser on text has a failure of its
    /// own — given audio with no speech in it, whisper sometimes hands back the context instead of
    /// nothing. That is caught and discarded, and the discards are counted; this switch is what an
    /// operator turns off if a host somehow makes it worse than the misheard names it fixes.
    /// </remarks>
    /// <panel>Whether the bot tells the recogniser what this host's servers are called before it
    /// listens. Names like <code>Ketchup</code> are otherwise heard as ordinary words — "catch-up" —
    /// because nothing has told it the server exists. The list is refreshed as servers are installed
    /// and removed.</panel>
    [LeafField("voicePrimeWithServerNames", "Prime with server names", Group = "voice",
        DependsOn = "voiceEnabled")]
    public bool PrimeWithServerNames { get; set; } = true;

    /// <summary>
    /// How long the bot keeps listening to somebody it has just asked a question, without the trigger.
    /// Zero switches it off.
    /// </summary>
    /// <remarks>
    /// Longer than the follow-up window because these are not the same wait: that one covers a pause
    /// while somebody finds their words, and this one covers reading the bot's question, thinking, and
    /// deciding. It is spent by a single utterance either way, so the number bounds how long an unused
    /// window lingers rather than how long a microphone stays open.
    /// </remarks>
    /// <panel>How long the bot keeps listening for your answer after it has asked you something,
    /// without needing the trigger phrase again — answering a question the bot just asked you should
    /// not require introducing yourself to it. It only ever covers the speaker who was asked, and one
    /// reply spends it. Set to 0 to always require the trigger.</panel>
    [LeafField("voiceReplyWindowSeconds", "Answer window", Group = "voice", Unit = "seconds",
        DependsOn = "voiceEnabled")]
    public int ReplyWindowSeconds { get; set; } = 20;

    /// <summary>
    /// Whether a staged action may be approved by saying so, as well as by pressing the button.
    /// </summary>
    /// <remarks>
    /// Only ever offered for a single staged action, and only on an unmistakable yes — anything else
    /// is asked about again rather than resolved toward approval. The buttons are posted either way
    /// and stay the answer of record, so switching this off removes an option and breaks nothing.
    /// </remarks>
    /// <panel>Whether you can approve a staged action by saying so — "yes", "go ahead", "do it" —
    /// instead of pressing the button, which is what you want when your hands are in a game. Everything
    /// waiting is named out loud before you are asked, so one yes covers all of it. Only a clear yes
    /// counts: anything the bot is unsure of is asked again rather than treated as approval. The
    /// buttons are always posted as well.</panel>
    [LeafField("voiceConfirmByVoice", "Approve out loud", Group = "voice", DependsOn = "voiceEnabled")]
    public bool ConfirmByVoice { get; set; } = true;

    /// <summary>
    /// Whether the bot says something short the moment it hears a request, before it has the answer.
    /// </summary>
    /// <remarks>
    /// Spoken alongside the work rather than before it, so it costs no latency — the turn starts
    /// first and the phrase plays while it runs. What it buys is that a person can tell the
    /// difference between a bot that is thinking and one that did not hear them, which from inside a
    /// voice channel are otherwise the same silence.
    /// </remarks>
    /// <panel>Whether the bot says something short — "Looking into it." — the moment it hears you,
    /// instead of going quiet until the answer is ready. An approved job that takes a while says so
    /// as it starts, rather than leaving you waiting without knowing anything happened.</panel>
    [LeafField("voiceAcknowledge", "Say something while working", Group = "voice",
        DependsOn = "voiceEnabled")]
    public bool Acknowledge { get; set; } = true;

    /// <summary>
    /// Whether the two listening states are marked with a tone instead of a spoken phrase.
    /// </summary>
    /// <remarks>
    /// A tone says the same thing in a fifth of the time and does not wear out with repetition, which
    /// a fixed phrase does. Off, the same moments are spoken instead — the state still has to be
    /// reported, since a bot waiting on somebody who does not know it is waiting eventually gets
    /// nothing.
    /// </remarks>
    /// <panel>Whether the bot marks its listening with two short tones rather than by talking. A
    /// rising tone means it is waiting for you to speak and a clock is running; a falling one means it
    /// has your request and is working on it. Switched off, those moments are spoken instead. Anything
    /// with something to tell you — a long job, a question it could not make out — is always
    /// spoken.</panel>
    [LeafField("voiceChimes", "Mark listening with tones", Group = "voice",
        DependsOn = "voiceEnabled")]
    public bool Chimes { get; set; } = true;

    /// <summary>
    /// How much of a sentence to read before it is finished, looking for the trigger. 0 waits for the
    /// whole sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognition normally runs on a finished sentence, so the earliest the bot can know it is being
    /// addressed is after the person has stopped talking — which puts the tone that says "go ahead"
    /// after the words it was meant to encourage. Reading the opening of the sentence while the rest
    /// is still being said moves it to where a person expects it.
    /// </para>
    /// <para>
    /// ⚠ <b>It costs a whole recognition pass.</b> Whisper pads what it is given to a fixed window, so
    /// reading a second and a half costs about what reading the sentence costs — and it is spent on
    /// every utterance long enough to qualify, including the ones nobody addressed to the bot. It is
    /// skipped outright whenever the recogniser is busy, so it can never delay a real answer, and what
    /// it buys when it is skipped is nothing at all.
    /// </para>
    /// <para>
    /// Long enough to contain the trigger and the speaker's run-up to it; shorter, and it reads a
    /// fragment of the first word.
    /// </para>
    /// </remarks>
    /// <panel>How much of a sentence the bot reads before you have finished saying it, to work out
    /// early that you are talking to it — which is what lets the "go ahead" tone arrive while you are
    /// still speaking rather than afterwards. It costs a full recognition pass on most of what is said
    /// in the channel, so set it to 0 on a busy host or a slow one. It never delays an answer: it is
    /// skipped whenever the recogniser is already working.</panel>
    [LeafField("voiceEarlyTriggerMs", "Spot the trigger early", Group = "voice",
        Min = 0, Max = 5000, Unit = "ms", DependsOn = "voiceEnabled")]
    public int EarlyTriggerMs { get; set; } = 1500;

    /// <panel>How long after somebody says the trigger on its own the bot keeps listening for what
    /// they actually wanted. It covers saying "hey assistant", pausing, and then asking.</panel>
    [LeafField("voiceFollowUpSeconds", "Wait after the trigger", Group = "voice",
        Min = 1, Max = 60, Unit = "s", DependsOn = "voiceEnabled")]
    public int FollowUpSeconds { get; set; } = 10;

    /// <summary>
    /// Whether everything recognised is written to the log, including what was not addressed to the
    /// bot.
    /// </summary>
    /// <remarks>
    /// Off, the bot logs only what somebody said to it, which is the right default and also makes a
    /// trigger that is not matching impossible to diagnose — the operator is asked to tune a phrase
    /// against evidence they cannot see. This is that evidence, and it is opt-in because switching it
    /// on writes down a room's private conversation.
    /// </remarks>
    /// <panel>Whether the log records everything said in the channel, not only what was addressed to
    /// the bot. Switch it on to find out how the recogniser is hearing your trigger phrase, and off
    /// again afterwards — while it is on, everything anybody says in the channel is written to this
    /// host's log.</panel>
    [LeafField("voiceLogTranscripts", "Log everything heard", Group = "voice",
        Risk = LeafRisk.Wiring, DependsOn = "voiceEnabled")]
    public bool LogTranscripts { get; set; } = false;

    /// <panel>Whether the bot says its answers out loud as well as posting them. Off, it still
    /// listens and still answers — in the voice channel's chat.</panel>
    [LeafField("voiceSpeak", "Answer out loud", Group = "voice", DependsOn = "voiceEnabled")]
    public bool Speak { get; set; } = true;

    /// <summary>
    /// Whether saying the trigger phrase while the bot is talking stops it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The trigger, and nothing weaker.</b> Somebody starting to speak is not an interruption in a
    /// voice channel — it is a room where people talk over each other, and treating that as a signal
    /// would silence the bot every time two people spoke at once. Requiring the phrase makes cutting
    /// in something a person does on purpose, which is the same bargain the trigger already is for
    /// being heard at all.
    /// </para>
    /// <para>
    /// Only the sound stops. The turn that produced the answer has already finished and the answer is
    /// already in the chat, so there is nothing to undo and nothing left half-done.
    /// </para>
    /// </remarks>
    /// <panel>Whether saying the trigger phrase while the bot is talking stops it mid-answer, so you
    /// do not have to wait out a long reply to ask something else. Only the trigger does it —
    /// ordinary talking over the bot does not, or a busy channel would cut it off constantly. The
    /// answer stays in the chat either way.</panel>
    [LeafField("voiceInterruptible", "Cut in while it is talking", Group = "voice",
        DependsOn = "voiceSpeak")]
    public bool Interruptible { get; set; } = true;

    /// <summary>The Kokoro model used to synthesise speech.</summary>
    /// <remarks>
    /// Beside the recognition model and outside the install prefix, for the same reason: the deploy
    /// syncs that prefix with <c>rsync --delete</c>.
    /// </remarks>
    /// <panel>The speech synthesis model file. Without it the bot answers in text only.</panel>
    [LeafField("voiceSpeechModelPath", "Synthesis model", Group = "voice", Type = LeafType.Path,
        DependsOn = "voiceSpeak")]
    public string SpeechModelPath { get; set; } = "/var/lib/kgsm-bot/models/kokoro.onnx";

    /// <summary>
    /// Which of Kokoro's voices to speak in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The English ones, listed best-first within each accent.</b> Kokoro ships voices for eight
    /// other languages and they are on disk beside these, but they expect text in those languages —
    /// offered here they would be twenty-odd ways to read an English answer badly. Anything Kokoro can
    /// load still works if it is set directly; what this list is, is the set worth choosing from.
    /// </para>
    /// <para>
    /// <b>Ordered by how much speech each was trained on, because that is what is audible.</b> The
    /// difference between the top of a group and the bottom is not accent or timbre — it is how
    /// synthetic the voice sounds, and it is not subtle.
    /// </para>
    /// </remarks>
    /// <panel>Which voice the bot speaks in. The first two letters are the accent and the speaker —
    /// <code>b</code> British, <code>a</code> American, then <code>f</code> or <code>m</code>. They are
    /// listed best-first within each accent, and the gap is worth hearing: the ones at the top of each
    /// group were trained on hours of speech and the ones at the bottom on minutes, which is the
    /// difference between a voice that sounds like a person and one that sounds like a synthesiser.
    /// Changing this restarts the bot, so it leaves any voice channel it is sitting in.</panel>
    [LeafField("voiceSpeechVoice", "Speaking voice", Group = "voice", DependsOn = "voiceSpeak",
        Type = LeafType.Enum, Values = [
            // British — bf_emma is the only one of these with hours of speech behind it.
            "bf_emma", "bf_isabella", "bf_alice", "bf_lily",
            "bm_george", "bm_fable", "bm_lewis", "bm_daniel",
            // American — af_heart and af_bella are the best-trained voices Kokoro ships at all.
            "af_heart", "af_bella", "af_nicole", "af_aoede", "af_kore", "af_sarah",
            "af_alloy", "af_nova", "af_sky", "af_jessica", "af_river",
            "am_fenrir", "am_michael", "am_puck", "am_echo", "am_eric",
            "am_liam", "am_onyx", "am_santa", "am_adam",
        ])]
    public string SpeechVoice { get; set; } = "bf_emma";

    /// <panel>Whether to synthesise speech on the graphics card. Around eight times faster than the
    /// processor and worth roughly 700MB of video memory; a host without a usable card falls back on
    /// its own.</panel>
    [LeafField("voiceSpeakUseGpu", "Synthesise on the GPU", Group = "voice", DependsOn = "voiceSpeak")]
    public bool SpeakUseGpu { get; set; } = true;
}
