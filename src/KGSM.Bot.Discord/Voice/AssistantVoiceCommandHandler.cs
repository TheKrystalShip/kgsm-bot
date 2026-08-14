using Discord;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Discord.Commands;
using KGSM.Bot.Core.Voice;
using KGSM.Bot.Infrastructure.Authorization;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Options;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Voice;

/// <summary>
/// Puts a spoken request to the kgsm-assistant leaf, and answers in the voice channel's own chat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Speaking is not authority.</b> The voice connection says which Discord account said something;
/// what that person may ask this host to do is the KGSM account theirs is connected to, resolved from
/// the same store every other surface reads. Being in the channel grants nothing, and neither does
/// having been the one who invited the bot.
/// </para>
/// <para>
/// <b>A voice channel is a room, so everybody in it shares one conversation.</b> That is the same
/// rule a thread follows and for the same reason: people talking in one are having a single
/// conversation and expect the assistant to be in it, rather than answering each person as though the
/// others had not spoken. Each utterance still carries who said it, so the assistant knows which of
/// them is asking.
/// </para>
/// <para>
/// <b>Said out loud and posted, not one or the other.</b> The chat message is the record and carries
/// the whole answer; the spoken form is a prefix of it, cut at a sentence. A staged action is offered
/// with the same buttons the @-mention surface posts, and the spoken reply says so rather than
/// asking for a spoken yes: approving out loud would be a second way to authorise a destructive
/// action, and the button re-derives authority at the click where a recogniser could not.
/// </para>
/// <para>
/// Speaking is best-effort throughout. No model, no card, or a broken output stream costs the audio
/// and nothing else — the answer is already in the channel, which is why nothing here treats a
/// failure to speak as a failure to answer.
/// </para>
/// </remarks>
public sealed class AssistantVoiceCommandHandler : IVoiceCommandHandler
{
    private const int DiscordMessageLimit = 2000;

    private readonly DiscordSocketClient _client;
    private readonly IAssistantTurnClient _assistant;
    private readonly IKgsmAccounts _accounts;
    private readonly ITextToSpeech _speech;
    private readonly IVoiceSessions _sessions;
    private readonly VoiceOptions _options;
    private readonly ILogger<AssistantVoiceCommandHandler> _logger;

    public AssistantVoiceCommandHandler(
        DiscordSocketClient client,
        IAssistantTurnClient assistant,
        IKgsmAccounts accounts,
        ITextToSpeech speech,
        IVoiceSessions sessions,
        IOptions<DiscordOptions> options,
        ILogger<AssistantVoiceCommandHandler> logger)
    {
        _client = client;
        _assistant = assistant;
        _accounts = accounts;
        _speech = speech;
        _sessions = sessions;
        _options = options.Value.Voice;
        _logger = logger;
    }

    public async ValueTask HandleAsync(VoiceCommand command, CancellationToken ct = default)
    {
        try
        {
            // Somebody said the trigger and then nothing a recogniser could make words of. There is
            // no question to put, and answering an empty one wastes a turn to produce a shrug.
            if (string.IsNullOrWhiteSpace(command.Text)) return;

            if (_client.GetChannel(command.ChannelId) is not IMessageChannel channel)
            {
                // A voice channel carries its own text chat, and without it there is nowhere to put
                // an answer — so the request is refused loudly here rather than answered into
                // nothing.
                _logger.LogWarning(
                    "Voice: {Speaker} asked something but channel {ChannelId} has no chat to answer in",
                    command.SpeakerName, command.ChannelId);
                return;
            }

            if (!_assistant.IsConfigured)
            {
                await channel.SendMessageAsync(
                    $"💤 {command.SpeakerName} asked me something, but there's no assistant set up on "
                    + "this host — the slash commands still work.");
                return;
            }

            AccountAnswer account = await _accounts.ResolveAsync(command.SpeakerId);
            if (!account.Allows(KgsmTier.Viewer))
            {
                // Said in the channel rather than dropped: from inside a voice call, a bot that hears
                // you and says nothing is indistinguishable from one that is broken.
                await channel.SendMessageAsync($"🎙️ {command.SpeakerName}: {account.Refusal(KgsmTier.Viewer)}");
                return;
            }

            _logger.LogInformation(
                "Voice: putting {Speaker}'s request to the assistant (account={Account}, tier={Tier})",
                command.SpeakerName, account.Account, account.Tier);

            await AnswerAsync(channel, command, account.Tier, ct);
        }
        catch (Exception ex)
        {
            // The contract is that this must not throw: it is called from the loop draining a live
            // audio stream, and one failed request must not end the voice session.
            _logger.LogError(ex, "Voice: failed to answer {Speaker}", command.SpeakerName);
        }
    }

    private async Task AnswerAsync(
        IMessageChannel channel, VoiceCommand command, KgsmTier tier, CancellationToken ct)
    {
        // Echoed before the answer, because a recogniser mishears and the person needs to see what
        // the bot thought they said — an answer about the wrong server is otherwise inexplicable.
        await channel.SendMessageAsync($"🎙️ **{command.SpeakerName}:** {command.Text}");

        Result<AssistantTurn> result;
        using (channel.EnterTypingState())
        {
            result = await _assistant.AskAsync(new AssistantAsk(
                command.SpeakerId.ToString(),
                command.SpeakerName,
                tier,
                command.ChannelId.ToString(),
                command.Text,
                RoomFor(command)), ct);
        }

        if (result.IsFailure)
        {
            await channel.SendMessageAsync($"⚠️ {result.Error}");
            await SayAsync(command, result.Error!, ct);
            return;
        }

        AssistantTurn turn = result.Value!;

        if (!string.IsNullOrWhiteSpace(turn.Text))
            await channel.SendMessageAsync(Truncate(turn.Text, DiscordMessageLimit));
        else if (turn.StagedActions.Count == 0)
            await channel.SendMessageAsync("🤔 I didn't have anything to say to that.");

        foreach (StagedAction staged in turn.StagedActions)
        {
            if (!StagedActionPrompt.CanBuild(staged))
            {
                await channel.SendMessageAsync(StagedActionPrompt.CannotBuild(staged));
                continue;
            }

            await channel.SendMessageAsync(
                StagedActionPrompt.Content(staged), components: StagedActionPrompt.Buttons(staged));
        }

        await SayAsync(command, SpokenAnswer(turn), ct);
    }

    /// <summary>
    /// What to say out loud: the reply, and where an offer to act has gone.
    /// </summary>
    /// <remarks>
    /// A staged action is named rather than read out in full, and the sentence points at the buttons.
    /// Somebody listening needs to know the bot did not just do it — and the one thing they must not
    /// be invited to do is approve it by speaking.
    /// </remarks>
    private static string SpokenAnswer(AssistantTurn turn)
    {
        if (turn.StagedActions.Count == 0) return turn.Text ?? string.Empty;

        string offer = turn.StagedActions.Count == 1
            ? $"I've put a confirmation in the chat for you to approve."
            : $"I've put {turn.StagedActions.Count} confirmations in the chat for you to approve.";

        return string.IsNullOrWhiteSpace(turn.Text) ? offer : $"{turn.Text} {offer}";
    }

    /// <summary>Says an answer in the channel it was asked in, if this host can speak at all.</summary>
    private async Task SayAsync(VoiceCommand command, string answer, CancellationToken ct)
    {
        if (!_options.Speak || !_speech.IsAvailable) return;

        string spoken = SpokenText.From(answer, Math.Max(40, _options.SpeakMaxCharacters));
        if (spoken.Length == 0) return;

        byte[]? audio = await _speech.SynthesizeAsync(spoken, ct);
        if (audio is null) return;

        Result said = await _sessions.SpeakAsync(command.GuildId, audio, ct);
        if (said.IsFailure)
            _logger.LogDebug("Voice: could not say the answer to {Speaker}: {Reason}",
                command.SpeakerName, said.Error);
    }

    /// <summary>
    /// The shared conversation a voice channel is.
    /// </summary>
    /// <remarks>
    /// Named by guild and channel, and marked as a voice room, so it cannot collide with the text
    /// channel of the same id and two hosts' rooms stay apart in a store that holds both.
    /// </remarks>
    private static string RoomFor(VoiceCommand command) => $"{command.GuildId}-v{command.ChannelId}";

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
