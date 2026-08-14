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
/// <b>A question the bot asks is answered without re-addressing it.</b> Having just spoken to
/// somebody, it waits for them — see <see cref="VoiceAttention"/>. That window is never opened
/// alongside a staged action, so it removes the trigger from ordinary conversation and never from
/// approving something.
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
    private readonly VoiceAttention _attention;
    private readonly VoiceOptions _options;
    private readonly ILogger<AssistantVoiceCommandHandler> _logger;

    public AssistantVoiceCommandHandler(
        DiscordSocketClient client,
        IAssistantTurnClient assistant,
        IKgsmAccounts accounts,
        ITextToSpeech speech,
        IVoiceSessions sessions,
        VoiceAttention attention,
        IOptions<DiscordOptions> options,
        ILogger<AssistantVoiceCommandHandler> logger)
    {
        _client = client;
        _assistant = assistant;
        _accounts = accounts;
        _speech = speech;
        _sessions = sessions;
        _attention = attention;
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
            // Spoken only from here. The @-mention surface asks the same assistant as the same
            // account and wants the written answer — the style describes where this reply lands, not
            // who asked for it, which is why it rides the turn instead of being configured once.
            result = await _assistant.AskAsync(new AssistantAsk(
                command.SpeakerId.ToString(),
                command.SpeakerName,
                tier,
                command.ChannelId.ToString(),
                command.Text,
                RoomFor(command),
                Spoken: true), ct);
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
        Await(command, turn);
    }

    /// <summary>
    /// Waits for an answer when the bot has just asked for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never while an action is staged</b>, even if the assistant's reply ends in a question. What
    /// the bot wants at that moment is a button press, and opening a listening window beside a pending
    /// confirmation invites somebody to approve it by saying yes — which is not what would happen, and
    /// is a thing to decide on deliberately rather than to inherit from a window opened for another
    /// purpose. The buttons remain the only way to approve.
    /// </para>
    /// <para>
    /// The window only removes the need to say the trigger. Whatever is said into it is put to the
    /// assistant exactly as a triggered request is, with the speaker's own authority re-derived at the
    /// turn.
    /// </para>
    /// </remarks>
    private void Await(VoiceCommand command, AssistantTurn turn)
    {
        if (_options.ReplyWindowSeconds <= 0) return;
        if (turn.StagedActions.Count > 0) return;
        if (!VoiceAttention.InvitesAnAnswer(turn.Text)) return;

        _attention.Expect(
            command.SpeakerId, command.ChannelId,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_options.ReplyWindowSeconds));

        _logger.LogInformation(
            "Voice: asked {Speaker} something — listening for their answer without the trigger",
            command.SpeakerName);
    }

    /// <summary>
    /// What to say out loud: the reply, and where an offer to act has gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A staged action is named rather than read out in full, and the sentence points at the buttons.
    /// Somebody listening needs to know the bot did not just do it — and the one thing they must not
    /// be invited to do is approve it by speaking.
    /// </para>
    /// <para>
    /// <b>The two halves say different things and neither repeats the other.</b> That an action is
    /// staged and waiting is the assistant's to say, and it does. That the thing to press is in the
    /// channel's chat is only this surface's to say — it knows about the buttons and the assistant does
    /// not. So the addition is about <em>where</em>, and grows into the full sentence only when the
    /// assistant said nothing at all, which is the one case where nobody has been told there is
    /// anything waiting.
    /// </para>
    /// </remarks>
    private static string SpokenAnswer(AssistantTurn turn)
    {
        if (turn.StagedActions.Count == 0) return turn.Text ?? string.Empty;

        bool many = turn.StagedActions.Count > 1;

        if (string.IsNullOrWhiteSpace(turn.Text))
            return many
                ? $"I've put {turn.StagedActions.Count} confirmations in the chat for you to approve."
                : "I've put a confirmation in the chat for you to approve.";

        return many ? $"{turn.Text} Approve them in the chat." : $"{turn.Text} Approve it in the chat.";
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
