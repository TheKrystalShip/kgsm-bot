using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Authorization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Acts on the assistant's memory of this channel, rather than asking it anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are commands and not something you can ask for.</b> A model told to forget the
/// conversation replies that it has, and remembers every word: a reply is the only thing a turn
/// produces, and the reply is not the thing being asked for. These reach the endpoint that owns the
/// stored conversation instead.
/// </para>
/// <para>
/// <b>The assistant decides what each one does and who may do it</b>, including refusing — clearing a
/// conversation a whole channel shares is not the same act as clearing your own, and only the
/// assistant knows which one this is. What comes back is its wording, shown as it arrives; there is no
/// second opinion formed here about what just happened.
/// </para>
/// <para>
/// <b>Answered in the open, not ephemerally.</b> Everything else this bot shows one person is a
/// question only they asked. Clearing a channel's conversation changes what everybody in it is
/// talking to, and doing that invisibly leaves the next person's answer looking like a fault.
/// </para>
/// </remarks>
// Viewer is the FLOOR, not the whole gate. Compacting is a maintenance action anybody in the channel
// may ask for; clearing a conversation a channel shares is refused by the assistant below operator,
// and that refusal is shown as it arrives. Gating both at operator here would take compaction away
// from the people most likely to notice a conversation getting long.
[RequireTier(KgsmTier.Viewer)]
[Group("conversation", "Manage what the assistant remembers of this channel")]
public class ConversationModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IAssistantTurnClient _assistant;
    private readonly IKgsmAccounts _accounts;
    private readonly ILogger<ConversationModule> _logger;

    public ConversationModule(
        IAssistantTurnClient assistant, IKgsmAccounts accounts, ILogger<ConversationModule> logger)
    {
        _assistant = assistant;
        _accounts = accounts;
        _logger = logger;
    }

    [SlashCommand("clear", "Forget this channel's conversation and start fresh")]
    public Task ClearAsync() => RunAsync("new");

    [SlashCommand("compact", "Summarise this channel's conversation to free up context")]
    public Task CompactAsync() => RunAsync("compact");

    private async Task RunAsync(string command)
    {
        await DeferAsync();

        if (!_assistant.IsConfigured)
        {
            await FollowupAsync("There's no assistant configured on this host.");
            return;
        }

        // The tier the caller's KGSM account holds right now, sent rather than judged: the assistant
        // gates its own commands, and a check here would be a second opinion able to disagree with it.
        AccountAnswer account = await _accounts.ResolveAsync(Context.User.Id);
        if (!account.Allows(KgsmTier.Viewer))
        {
            await FollowupAsync(account.Refusal(KgsmTier.Viewer));
            return;
        }

        Result<string> ran = await _assistant.RunCommandAsync(command, new AssistantAsk(
            Context.User.Id.ToString(),
            Context.User.Username,
            account.Tier,
            // The channel is the conversation, exactly as it is when somebody @-mentions the bot —
            // this has to name the same one the next question will continue, or it manages a
            // conversation nobody is having.
            Context.Channel.Id.ToString(),
            Prompt: string.Empty,
            RoomFor(Context.Channel)));

        _logger.LogInformation(
            "Assistant /{Command} by {User} in {Channel}: {Outcome}",
            command, Context.User.Username, Context.Channel.Id, ran.IsSuccess ? "done" : ran.Error);

        await FollowupAsync(ran.IsSuccess ? ran.Value! : $"⚠️ {ran.Error}");
    }

    /// <summary>
    /// The shared room this channel is, when it is one — composed exactly as the @-mention surface
    /// composes it, because a command that named a different room would clear a conversation nobody
    /// is in.
    /// </summary>
    private static string? RoomFor(ISocketMessageChannel channel) =>
        channel is SocketThreadChannel thread ? $"{thread.Guild.Id}-{thread.Id}" : null;
}
