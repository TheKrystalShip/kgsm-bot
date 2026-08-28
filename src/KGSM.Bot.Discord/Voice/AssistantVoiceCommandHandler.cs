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
using TheKrystalShip.KGSM.Speech;

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
/// the whole answer; the spoken form is the same answer with its markup stripped. A staged action is
/// offered with the same buttons the @-mention surface posts, and the spoken reply says so rather than
/// asking for a spoken yes: approving out loud would be a second way to authorise a destructive
/// action, and the button re-derives authority at the click where a recogniser could not.
/// </para>
/// <para>
/// <b>The answer is read out as it is written.</b> Each sentence is synthesised and played the moment
/// the assistant finishes writing it, so the room hears the opening of a long answer while the model
/// is still producing the rest of it — see <see cref="SpokenRecital"/>. Nothing about the answer
/// changes: the whole reply is spoken, in order, and only when each part of it goes out is different.
/// The question is put over the frame stream for that reason alone, so a host that cannot speak takes
/// the buffered reply and pays nothing for the machinery.
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
    private readonly IVoiceChimes _chimes;
    private readonly VoiceAttention _attention;
    private readonly VoiceOptions _options;
    private readonly ILogger<AssistantVoiceCommandHandler> _logger;

    public AssistantVoiceCommandHandler(
        DiscordSocketClient client,
        IAssistantTurnClient assistant,
        IKgsmAccounts accounts,
        ITextToSpeech speech,
        IVoiceSessions sessions,
        IVoiceChimes chimes,
        VoiceAttention attention,
        IOptions<DiscordOptions> options,
        ILogger<AssistantVoiceCommandHandler> logger)
    {
        _client = client;
        _assistant = assistant;
        _accounts = accounts;
        _speech = speech;
        _sessions = sessions;
        _chimes = chimes;
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

            // A reply to a staged action is answered here rather than put to the assistant as a
            // question: "go ahead" is not a prompt, it is a decision about a specific grant.
            if (command.Answering is { For: VoiceWaitingFor.Confirmation } waiting)
            {
                await DecideAsync(channel, command, waiting, account.Tier, ct);
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

        // Asked OF the conversation rather than into it, so it is read before a turn exists. Putting
        // "forget everything" to the model gets a reply saying it has, from something that remembers
        // every word — the reply is all a turn can produce, and the reply is not what was asked for.
        SpokenConversationCommand asked = SpokenConversationCommands.Read(command.Text);
        if (asked != SpokenConversationCommand.None)
        {
            await RunConversationCommandAsync(channel, command, tier, asked, ct);
            return;
        }

        // Started, not awaited. The turn runs while this plays, so the first sound arrives in about
        // a tenth of a second instead of after the model has finished thinking — awaiting it here
        // would add a second to every request in order to appear faster.
        Task acknowledged = AcknowledgeHeardAsync(command, ct);

        // The answer is read out as it is written: each sentence is synthesised and played the moment
        // the assistant finishes it, rather than after the whole turn. Null where this host cannot
        // speak at all or the bot is in no channel to speak in, and the turn then takes the buffered
        // reply exactly as a question typed in a text channel does.
        await using SpokenRecital? recital = BeginRecital(command, acknowledged, ct);

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
                Spoken: true), new AssistantStream(Reply: recital), ct);
        }

        // Waited for only now, so the answer is never written into the middle of the acknowledgement.
        await acknowledged;

        // Whatever the reply ended on, said too — a last sentence with no full stop after it is still
        // part of the answer. True when any of the reply went out as it arrived, which is what decides
        // whether there is anything left to say below.
        bool reading = recital?.Flush() ?? false;

        if (result.IsFailure)
        {
            await channel.SendMessageAsync($"⚠️ {result.Error}");
            await SayThroughAsync(command, recital, result.Error!, ct);
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

        // Opened before the answer is spoken, so somebody who replies the instant it stops talking is
        // already being listened to.
        bool confirming = AwaitConfirmation(command, turn);

        // Only what has not already been read out: the reply went out sentence by sentence, so all
        // that is owed is the part this surface writes itself — where the buttons are. An assistant
        // whose frames carried no reply text leaves nothing spoken, and the finished answer is said
        // whole.
        await SayThroughAsync(command, recital, reading ? Offer(turn) : SpokenAnswer(turn), ct);

        bool answering = Await(command, turn);

        // Played after the question rather than with the window it belongs to, because the two mark
        // different things: the window opens early on purpose so nothing said over the tail of the
        // answer is lost, and the tone marks where a person's turn actually begins.
        if (confirming || answering)
            await _chimes.PlayAsync(command.GuildId, VoiceChime.Listening, ct);
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
    /// <returns>Whether a window was opened.</returns>
    private bool Await(VoiceCommand command, AssistantTurn turn)
    {
        if (_options.ReplyWindowSeconds <= 0) return false;
        if (turn.StagedActions.Count > 0) return false;
        if (!VoiceAttention.InvitesAnAnswer(turn.Text)) return false;

        Expect(command, new VoiceWaiting(VoiceWaitingFor.Answer, Until()));

        _logger.LogInformation(
            "Voice: asked {Speaker} something — listening for their answer without the trigger",
            command.SpeakerName);

        return true;
    }

    /// <summary>
    /// Listens for a yes or a no about an action that has just been staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The buttons are posted either way and remain the answer of record.</b> Nothing here removes
    /// a way to approve; it adds one for a person whose hands are in a game. Both redeem the same
    /// grants, so whichever happens first wins and the assistant refuses the other.
    /// </para>
    /// <para>
    /// <b>One yes covers everything the turn staged.</b> A request can stage several — "uninstall both
    /// starbound instances" stages two — and the person who asked for both and then agreed has
    /// answered about both. What is spoken back names them before the answer is given, so the yes is
    /// never about a set nobody described; the ambiguity worth refusing would be a yes arriving for
    /// actions the speaker did not ask for, and there is no path here that produces one.
    /// </para>
    /// </remarks>
    /// <returns>Whether a window was opened.</returns>
    private bool AwaitConfirmation(VoiceCommand command, AssistantTurn turn)
    {
        if (_options.ConfirmByVoice is false || _options.ReplyWindowSeconds <= 0) return false;
        if (turn.StagedActions.Count == 0) return false;

        IReadOnlyList<string> tokens = [.. turn.StagedActions.Select(a => a.Token)];

        Expect(command, new VoiceWaiting(
            VoiceWaitingFor.Confirmation, Until(), tokens, DescribeAll(turn.StagedActions),
            turn.StagedActions.Select(a => a.Kind).FirstOrDefault(SpokenAcknowledgement.IsSlow)
                ?? turn.StagedActions[0].Kind));

        _logger.LogInformation(
            "Voice: waiting for {Speaker} to confirm {Count} action(s) out loud — {What}",
            command.SpeakerName, tokens.Count, DescribeAll(turn.StagedActions));

        return true;
    }

    /// <summary>
    /// Reads a spoken reply to a staged action, and does only what it plainly says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anything that is not unmistakably a yes is asked about again.</b> A recogniser mishears, and
    /// the reply that matters here approves something destructive — so "I could not tell" is a real
    /// outcome with its own words, never rounded to the nearer of yes and no. It is said out loud as
    /// well as posted, because the person is not looking at the screen.
    /// </para>
    /// <para>
    /// <b>Asking again is bounded.</b> After a couple of attempts the window is left shut and the
    /// prompt in the chat stands — a bot that keeps asking is worse than one that stops, and nothing
    /// was lost, since the buttons were posted the moment the action was staged.
    /// </para>
    /// </remarks>
    private async Task DecideAsync(
        IMessageChannel channel, VoiceCommand command, VoiceWaiting waiting, KgsmTier tier,
        CancellationToken ct)
    {
        SpokenIntent intent = SpokenIntents.Read(command.Text);

        _logger.LogInformation(
            "Voice: read {Speaker}'s \"{Said}\" as {Intent} for the staged action",
            command.SpeakerName, command.Text, intent);

        if (intent == SpokenIntent.Decline)
        {
            // Nothing is redeemed and nothing is cancelled server-side: an unredeemed grant simply
            // expires, which is the same thing the Cancel button does.
            await channel.SendMessageAsync($"🎙️ **{command.SpeakerName}:** {command.Text}\n❌ Cancelled — I won't do it.");
            await SayAsync(command, "Alright, I won't.", ct);
            return;
        }

        if (intent == SpokenIntent.Unclear)
        {
            // Neither a yes nor a no — but if they addressed the bot and asked it something, they
            // have moved on rather than failed to answer. The offer is abandoned, which costs
            // nothing: the grant is never redeemed and the buttons are still in the chat.
            if (command.Triggered && command.Text.Length > 0)
            {
                _logger.LogInformation(
                    "Voice: {Speaker} asked something else instead of answering — leaving the offer standing",
                    command.SpeakerName);

                await AnswerAsync(channel, command, tier, ct);
                return;
            }

            await AskAgainAsync(channel, command, waiting, ct);
            return;
        }

        await channel.SendMessageAsync($"🎙️ **{command.SpeakerName}:** {command.Text}");

        // A confirmed install was measured taking thirty-two seconds, every one of them silent after
        // somebody said "go ahead". Said before the work rather than after it, and warning about the
        // wait when the verb is one that has a wait worth warning about.
        Task acknowledged = AcknowledgeAsync(
            command, SpokenAcknowledgement.WhileWorking(waiting.Kind), ct);

        // Each grant is redeemed on its own, because that is what a grant is — the assistant judges
        // authority and validity per action, and one being refused says nothing about the next.
        var spoken = new List<string>();
        var failed = 0;

        foreach (string token in waiting.Tokens ?? [])
        {
            Result<AssistantOutcome> done = await _assistant.ConfirmAsync(
                new AssistantApproval(command.SpeakerId.ToString(), command.SpeakerName, tier, token), ct);

            if (done.IsFailure)
            {
                // The assistant is the gate and it refused — an expired grant, one already redeemed,
                // or an authority this person no longer holds. Reported as it came, not softened.
                failed++;
                await channel.SendMessageAsync($"⚠️ {done.Error}");
                spoken.Add(done.Error!);
                continue;
            }

            AssistantOutcome outcome = done.Value!;
            if (!outcome.Success) failed++;
            await channel.SendMessageAsync($"{(outcome.Success ? "✅" : "⚠️")} {outcome.Text}");
            spoken.Add(outcome.Text);
        }

        await acknowledged;

        // Said as one sentence rather than one per action: each was already posted, and a speaker
        // reading out four outcomes in turn is a minute of talking nobody can interrupt. The count
        // is the part that cannot be got from listening to the first one.
        await SayAsync(command, Summarise(spoken, failed), ct);
    }

    /// <summary>
    /// What to say once, about however many actions were just carried out.
    /// </summary>
    /// <remarks>
    /// One outcome is read as it came. Several are counted, because the whole reason a person
    /// approved them out loud is that they are not reading the screen — and a count that names the
    /// failures is the one fact they cannot recover by listening to any single outcome. Never claims
    /// success it did not see: the failures are counted from what each redemption actually returned.
    /// </remarks>
    private static string Summarise(List<string> outcomes, int failed) => outcomes switch
    {
        [] => "Nothing was waiting to be confirmed.",
        [string only] => only,
        _ when failed == 0 => $"Done — all {outcomes.Count} of them.",
        _ when failed == outcomes.Count => $"None of the {outcomes.Count} went through. It's in the chat.",
        _ => $"{outcomes.Count - failed} of {outcomes.Count} went through; {failed} didn't. It's in the chat.",
    };

    /// <summary>Says it could not tell, and listens once more — up to a point.</summary>
    private async Task AskAgainAsync(
        IMessageChannel channel, VoiceCommand command, VoiceWaiting waiting, CancellationToken ct)
    {
        const int MostAttempts = 3;

        string about = waiting.Describes is { Length: > 0 } what ? $" about {what}" : string.Empty;

        if (waiting.Asked >= MostAttempts)
        {
            string gaveUp = $"I still didn't get a clear yes or no{about}. "
                + "The buttons in the chat are still there.";
            await channel.SendMessageAsync($"🎙️ **{command.SpeakerName}:** {command.Text}\n🤔 {gaveUp}");
            await SayAsync(command, gaveUp, ct);
            return;
        }

        Expect(command, waiting with { Until = Until(), Asked = waiting.Asked + 1 });

        string again = $"Sorry, I didn't catch a clear yes or no{about}. Say yes to go ahead, or no to cancel.";
        await channel.SendMessageAsync($"🎙️ **{command.SpeakerName}:** {command.Text}\n🤔 {again}");

        // Spoken, never a tone: a tone cannot say WHY it is asking again, and a rising tone on its own
        // after a failed answer reads as "go ahead" when what happened is the opposite. The reason is
        // the whole content of this message.
        await SayAsync(command, again, ct);

        await _chimes.PlayAsync(command.GuildId, VoiceChime.Listening, ct);
    }

    /// <summary>
    /// Marks the moment a request was taken, while the answer is still being worked out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A tone where there is nothing to say.</b> All this moment conveys is "I have it" — no
    /// detail, no reason, nothing a sentence could carry that a falling tone cannot. It is also the
    /// most repeated event on the surface, and a fixed phrase heard for the twentieth time has stopped
    /// being information; a tone does not wear out that way, and it costs no synthesis, so it arrives
    /// immediately rather than after a model has produced a waveform.
    /// </para>
    /// <para>
    /// Spoken instead where tones are switched off. The state still has to be reported either way: a
    /// bot that is thinking and one that never heard you are the same silence.
    /// </para>
    /// </remarks>
    private async Task AcknowledgeHeardAsync(VoiceCommand command, CancellationToken ct)
    {
        if (!_options.Acknowledge) return;

        if (_options.Chimes)
        {
            await _chimes.PlayAsync(command.GuildId, VoiceChime.Working, ct);
            return;
        }

        await AcknowledgeAsync(command, SpokenAcknowledgement.WhileThinking(), ct);
    }

    /// <summary>
    /// Says a short thing immediately, so somebody knows they were heard.
    /// </summary>
    /// <remarks>
    /// Never throws and never blocks the work it accompanies — it is started and awaited later, and
    /// the output stream serialises writes anyway, so the answer queues behind it rather than
    /// overlapping it. An acknowledgement that fails to be said costs nothing: the answer it was
    /// standing in front of is still coming.
    /// </remarks>
    private async Task AcknowledgeAsync(VoiceCommand command, string phrase, CancellationToken ct)
    {
        if (!_options.Acknowledge) return;

        try
        {
            await SayAsync(command, phrase, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: could not acknowledge {Speaker}", command.SpeakerName);
        }
    }

    private void Expect(VoiceCommand command, VoiceWaiting waiting) =>
        _attention.Expect(command.SpeakerId, command.ChannelId, waiting);

    private DateTimeOffset Until() =>
        DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_options.ReplyWindowSeconds);

    /// <summary>The action in the words somebody would use for it.</summary>
    private static string Describe(StagedAction staged) =>
        staged.InstanceName is { Length: > 0 } instance
            ? $"{staged.Kind.Replace('_', ' ')} {instance}"
            : staged.Kind.Replace('_', ' ');

    /// <summary>Everything a turn staged, as one phrase to ask about.</summary>
    private static string DescribeAll(IReadOnlyList<StagedAction> staged) => staged.Count switch
    {
        0 => string.Empty,
        1 => Describe(staged[0]),
        _ => $"{string.Join(", ", staged.Take(staged.Count - 1).Select(Describe))} "
             + $"and {Describe(staged[^1])}",
    };

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
    private string SpokenAnswer(AssistantTurn turn)
    {
        string text = turn.Text ?? string.Empty;
        string offer = Offer(turn);

        return text.Length == 0 ? offer
            : offer.Length == 0 ? text
            : $"{text} {offer}";
    }

    /// <summary>
    /// The half of a spoken answer that is this surface's own: where an offer to act has gone. Empty
    /// when the turn staged nothing.
    /// </summary>
    /// <remarks>
    /// Separated from the reply because the two are delivered at different moments. The reply is read
    /// out as the assistant writes it; this can only be said once the turn has finished, since until
    /// then there is nothing to say about what it staged.
    /// </remarks>
    private string Offer(AssistantTurn turn)
    {
        if (turn.StagedActions.Count == 0) return string.Empty;

        bool many = turn.StagedActions.Count > 1;

        bool byVoice = _options.ConfirmByVoice && _options.ReplyWindowSeconds > 0;

        // Named before the answer is asked for, so a yes is never about a set nobody heard described.
        // With several, the count is what the listener needs — it is the one thing they cannot tell
        // from the offer itself, and it is what their yes is about to cover.
        string offer = byVoice
            ? many
                ? $"That's {turn.StagedActions.Count} things: {DescribeAll(turn.StagedActions)}. "
                  + "Say yes to do all of them, or no to cancel."
                : "Say yes to go ahead, or no to cancel."
            : many
                ? $"I've put {turn.StagedActions.Count} confirmations in the chat for you to approve."
                : "I've put a confirmation in the chat for you to approve.";

        if (string.IsNullOrWhiteSpace(turn.Text)) return offer;

        return byVoice || many ? offer : "Approve it in the chat.";
    }

    /// <summary>
    /// Clears or compacts the voice channel's conversation, and says what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The assistant's wording, not this bot's.</b> What clearing means differs between a shared
    /// room and one person's own chat, and whether this speaker may do it at all is the assistant's
    /// answer to give — a refusal is read out exactly as it arrives, rather than being turned into a
    /// sentence written here that guesses at the reason.
    /// </para>
    /// <para>
    /// <b>Said out loud as well as posted</b>, because the person asking is in a voice channel and
    /// everyone else in it has just had their conversation cleared out from under them. A room whose
    /// memory silently vanishes is one where the next answer looks like a fault.
    /// </para>
    /// </remarks>
    private async Task RunConversationCommandAsync(
        IMessageChannel channel, VoiceCommand command, KgsmTier tier,
        SpokenConversationCommand asked, CancellationToken ct)
    {
        string name = asked == SpokenConversationCommand.Compact ? "compact" : "new";

        _logger.LogInformation(
            "Voice: {Speaker} asked to {Command} the conversation in {Channel}",
            command.SpeakerName, name, command.ChannelId);

        Result<string> ran = await _assistant.RunCommandAsync(name, new AssistantAsk(
            command.SpeakerId.ToString(),
            command.SpeakerName,
            tier,
            command.ChannelId.ToString(),
            command.Text,
            RoomFor(command),
            Spoken: true), ct);

        string said = ran.IsSuccess ? ran.Value! : ran.Error!;

        await channel.SendMessageAsync(ran.IsSuccess ? said : $"⚠️ {said}");
        await SayAsync(command, said, ct);
    }

    /// <summary>
    /// Opens the recital an answer is read out through, or null when nothing here can be spoken.
    /// </summary>
    /// <remarks>
    /// <b><c>Voice:Speak</c> is still the only gate on speaking at all.</b> Off, or with no
    /// synthesiser on the host, there is no recital — and with no recital the turn asks for the
    /// buffered reply, so a host that cannot speak pays nothing at all for the machinery that
    /// speaks as it goes.
    /// </remarks>
    private SpokenRecital? BeginRecital(VoiceCommand command, Task acknowledged, CancellationToken ct)
    {
        if (!_options.Speak || !_speech.IsAvailable) return null;

        IVoiceRecital? held = _sessions.BeginRecital(command.GuildId);
        if (held is null) return null;

        return new SpokenRecital(held, _speech, acknowledged, command.SpeakerName, _logger, ct);
    }

    /// <summary>
    /// Says a last thing as part of the answer it belongs to, and waits for the whole of it.
    /// </summary>
    /// <remarks>
    /// <b>Through the recital, not beside it.</b> Somebody who cuts in has cut off the answer, and
    /// a sentence spoken outside the recital would survive that and talk over them. Without one —
    /// a host that cannot speak, or a turn read out in one go — this is the plain say it always was.
    /// </remarks>
    private async Task SayThroughAsync(
        VoiceCommand command, SpokenRecital? recital, string answer, CancellationToken ct)
    {
        if (recital is null)
        {
            await SayAsync(command, answer, ct);
            return;
        }

        recital.Say(answer);
        await recital.FinishAsync();
    }

    /// <summary>Says an answer in the channel it was asked in, if this host can speak at all.</summary>
    private async Task SayAsync(VoiceCommand command, string answer, CancellationToken ct)
    {
        if (!_options.Speak || !_speech.IsAvailable) return;

        string spoken = SpokenSentences.Whole(answer);
        if (spoken.Length == 0) return;

        byte[]? audio = await _speech.SynthesizeAsync(spoken, ct);
        if (audio is null) return;

        // No wait limit: an answer is worth having whenever it lands, unlike a tone whose meaning is
        // the moment it plays.
        Result said = await _sessions.SpeakAsync(command.GuildId, audio, ct: ct);
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
