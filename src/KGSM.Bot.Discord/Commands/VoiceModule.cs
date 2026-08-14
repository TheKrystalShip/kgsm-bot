using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Voice;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Puts the bot in a voice channel, and takes it out again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Operator, and not because it changes anything.</b> These commands act on no server at all, so
/// they are not <c>[Mutating]</c> — what puts them here is that the bot in a voice channel hears
/// everybody in the room, including people who never addressed it. That is the same reason
/// <c>/logs</c> sits at operator while changing nothing: the gate is on what it exposes, not on what
/// it alters.
/// </para>
/// <para>
/// <b>The bot goes where the person asking already is.</b> There is no channel argument: somebody
/// who has to be in the channel to invite the bot into it cannot point it at a conversation they are
/// not part of, and everybody who will be heard can see who brought it in.
/// </para>
/// </remarks>
[RequireTier(KgsmTier.Operator)]
[Group("voice", "Have the bot listen in a voice channel")]
public class VoiceModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IVoiceSessions _voice;
    private readonly IVoiceTally _tally;
    private readonly ILogger<VoiceModule> _logger;

    public VoiceModule(IVoiceSessions voice, IVoiceTally tally, ILogger<VoiceModule> logger)
    {
        _voice = voice;
        _tally = tally;
        _logger = logger;
    }

    [SlashCommand("join", "Join the voice channel you are in and start listening")]
    public async Task JoinAsync()
    {
        await DeferAsync(ephemeral: true);

        if (!_voice.IsEnabled)
        {
            await FollowupAsync("Voice listening is switched off on this host.", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser user || user.VoiceChannel is null)
        {
            await FollowupAsync("Join a voice channel first — I'll come to the one you're in.", ephemeral: true);
            return;
        }

        Result<VoiceSession> result = await _voice.JoinAsync(Context.Guild.Id, user.VoiceChannel.Id);
        if (result.IsFailure)
        {
            await FollowupAsync(result.Error, ephemeral: true);
            return;
        }

        _logger.LogInformation(
            "Voice: {User} brought the bot into {Channel}", user.Username, result.Value!.ChannelName);

        // Two messages, because they answer two different people. The ephemeral one confirms to
        // whoever ran the command; the channel one tells the room it is being listened to, which is
        // the only notice the people who did not run it ever get.
        await FollowupAsync($"Listening in **{result.Value!.ChannelName}**.", ephemeral: true);
        await Context.Channel.SendMessageAsync(
            $"🎙️ I'm listening in **{result.Value.ChannelName}** — {user.Mention} asked me to join. "
            + "Everyone in the channel is heard while I'm there. `/voice leave` sends me away.");
    }

    [SlashCommand("leave", "Stop listening and leave the voice channel")]
    public async Task LeaveAsync()
    {
        await DeferAsync(ephemeral: true);

        Result result = await _voice.LeaveAsync(Context.Guild.Id);
        await FollowupAsync(
            result.IsSuccess ? "Left the voice channel." : result.Error, ephemeral: true);
    }

    [SlashCommand("status", "Where the bot is listening, and what it has heard")]
    public async Task StatusAsync()
    {
        VoiceSession? session = _voice.Describe(Context.Guild.Id);

        if (session is null)
        {
            await RespondAsync(
                _voice.IsEnabled
                    ? "I'm not in a voice channel here."
                    : "Voice listening is switched off on this host.",
                ephemeral: true);
            return;
        }

        VoiceCounts counts = _tally.Read();

        // The four numbers in the order speech passes through them, so the one that drops to zero is
        // the stage that is failing. A single "heard" count cannot distinguish a bot that understands
        // nobody from one that understands everybody and is never addressed.
        var embed = new EmbedBuilder()
            .WithTitle("🎙️ Voice")
            .WithColor(Color.Green)
            .AddField("Channel", session.ChannelName, inline: true)
            .AddField("Speakers", session.Speakers.ToString(), inline: true)
            .AddField("Heard here", $"{session.Utterances} utterance(s)", inline: true)
            .AddField(
                "Since the bot started",
                $"**{counts.Heard}** heard → **{counts.Recognised}** recognised → "
                + $"**{counts.Addressed}** addressed → **{counts.Answered}** answered");

        if (counts.Echoed > 0)
            embed.AddField(
                "Discarded",
                $"{counts.Echoed} transcript(s) were the primed server names coming back, not speech.");

        if (counts.Diagnosis is string diagnosis)
            embed.WithColor(Color.Orange).AddField("What that means", diagnosis);

        await RespondAsync(
            embed: embed.WithFooter($"Joined {session.JoinedAt:HH:mm:ss} UTC").Build(), ephemeral: true);
    }
}
