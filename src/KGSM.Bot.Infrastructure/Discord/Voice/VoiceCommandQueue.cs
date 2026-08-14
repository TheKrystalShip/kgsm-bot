using System.Threading.Channels;

using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Infrastructure.Discord.Voice;

/// <summary>
/// Carries a recognised request from the audio path to whatever answers it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Answering must not happen on the loop that hears.</b> A turn takes seconds — the model, its
/// tools, then synthesis — and until this queue existed all of it ran inside the tick that closes
/// utterances. One person asking a question stopped every other speaker's sentence from being
/// finished for the whole time it took to answer, in a channel where other people were still
/// talking.
/// </para>
/// <para>
/// <b>It is also what makes the wiring acyclic.</b> The session owns the connection, so answering
/// out loud has to go back through it; recognising is downstream of the session and answering is
/// downstream of recognising. Wired directly that is a circle, and the container says so at startup.
/// A queue is the honest break rather than a trick to satisfy the container: hearing and answering
/// really are two pieces of work with a handoff between them.
/// </para>
/// <para>
/// <b>Bounded, and the oldest goes first when it fills.</b> A request nobody has got to in a while is
/// the least worth answering — the person has moved on or asked again — and an unbounded queue in
/// front of a model that answers in seconds is a way to be minutes behind a conversation.
/// </para>
/// </remarks>
public sealed class VoiceCommandQueue
{
    private readonly Channel<VoiceCommand> _channel = Channel.CreateBounded<VoiceCommand>(
        new BoundedChannelOptions(capacity: 16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public ChannelReader<VoiceCommand> Reader => _channel.Reader;

    /// <summary>Hands a request over. Never blocks the caller and never throws.</summary>
    public bool Enqueue(VoiceCommand command) => _channel.Writer.TryWrite(command);
}

/// <summary>
/// Answers spoken requests, one at a time, off the audio path.
/// </summary>
/// <remarks>
/// One at a time on purpose. Two turns at once would contend for the same model and arrive
/// interleaved in the channel, and the voice connection can only say one of them out loud anyway.
/// </remarks>
public sealed class VoiceCommandWorker(
    VoiceCommandQueue queue,
    IVoiceCommandHandler handler,
    ILogger<VoiceCommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (VoiceCommand command in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await handler.HandleAsync(command, stoppingToken);
                }
                catch (Exception ex)
                {
                    // One request failing is not the surface failing. The next one is already queued.
                    logger.LogError(ex, "Voice: failed to answer {Speaker}", command.SpeakerName);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
