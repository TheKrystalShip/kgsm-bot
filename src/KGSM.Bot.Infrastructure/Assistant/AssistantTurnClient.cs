using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Relay;

namespace KGSM.Bot.Infrastructure.Assistant;

/// <summary>
/// Talks to the kgsm-assistant leaf over its HTTP surface, forwarding the Discord user who asked.
/// </summary>
/// <remarks>
/// <para>
/// The headers that carry who someone is and what they may do are written by the assistant's own
/// relay package, not by this class — the Control Panel API forwards identity through the very same
/// writer. Two surfaces hand-rolling that is how they come to disagree about a person's authority,
/// which is the one disagreement an authority header cannot survive.
/// </para>
/// <para>
/// Which transport a question takes is decided by what the caller can use, not by configuration. A
/// surface that only posts a finished message takes the buffered reply, because that is what a
/// Discord message is; one that renders the work as it happens, or speaks the answer sentence by
/// sentence, reads the frames.
/// </para>
/// </remarks>
public sealed class AssistantTurnClient : IAssistantTurnClient, IDisposable
{
    // Liveness only. Short on purpose: it is asked in front of a person who is waiting, and a hung
    // assistant must not become a hung bot.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AssistantRelay _relay;
    private readonly ILogger<AssistantTurnClient> _logger;
    private readonly bool _hasBaseUrl;

    public AssistantTurnClient(IOptions<AssistantOptions> options, ILogger<AssistantTurnClient> logger)
        : this(options.Value, transport: null, logger) { }

    /// <summary>Test seam: supply the transport, so what goes on the wire can be asserted.</summary>
    internal AssistantTurnClient(
        AssistantOptions settings, HttpMessageHandler? transport, ILogger<AssistantTurnClient> logger)
    {
        _logger = logger;
        _relay = new AssistantRelay(settings.RelaySecret, RelayLeaf.Bot);

        _http = new HttpClient(transport ?? new SocketsHttpHandler
        {
            // Recycle pooled connections so a process-lifetime client cannot pin a stale one across
            // an assistant redeploy.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // A turn is the only slow call; the probe imposes its own, shorter budget with a linked
            // token, so this ceiling never shortens it.
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
        };

        if (Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            _http.BaseAddress = baseUri;
            _hasBaseUrl = true;
        }
        else if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            // Configured but unusable is a mistake worth naming, not a quiet "off": the operator meant
            // to have a conversational surface and does not have one.
            _logger.LogError(
                "Assistant:BaseUrl is not a valid absolute URL ({BaseUrl}) — the conversational surface is off",
                settings.BaseUrl);
        }

        // An assistant to reach and no secret to reach it with. The secret is normally the host's own,
        // minted on first look, so an empty one means the file could be neither read nor created — a
        // provisioning fault that is otherwise invisible until somebody @-mentions the bot and the
        // assistant refuses. Name the file: the usual cause is a directory owned by somebody else.
        if (_hasBaseUrl && !_relay.IsConfigured)
            _logger.LogWarning(
                "no relay secret — the assistant at {BaseUrl} will refuse every question this bot asks on "
                + "someone's behalf. The host's own secret is {Path}, which could not be read or created; "
                + "check that directory is owned by the account this bot runs as",
                settings.BaseUrl, settings.RelaySecretPath);
    }

    /// <summary>
    /// True when there is both an address to reach the assistant at and a secret it will accept.
    /// Either missing leaves the surface off, rather than producing questions the assistant refuses.
    /// </summary>
    public bool IsConfigured => _hasBaseUrl && _relay.IsConfigured;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        IsConfigured
            ? AssistantHealthProbe.CheckAsync(_http, ct, _logger, ProbeTimeout)
            : Task.FromResult(false);

    public Task<Result<AssistantTurn>> AskAsync(AssistantAsk ask, CancellationToken ct = default) =>
        AskAsync(ask, AssistantStream.None, ct);

    public Task<Result<AssistantTurn>> AskAsync(
        AssistantAsk ask, IProgress<AssistantActivity>? activity, CancellationToken ct = default) =>
        AskAsync(ask, new AssistantStream(activity), ct);

    /// <summary>
    /// Puts the question, streaming the turn when somebody is watching it and taking the buffered
    /// answer when nobody is.
    /// </summary>
    /// <remarks>
    /// Two transports for one question, chosen by whether the caller can use what the streamed one
    /// carries. The buffered contract returns the finished reply and the actions it staged, which is
    /// everything a surface that only posts an answer needs; the frames additionally say what is being
    /// consulted while it happens and hand over the reply as it is written, which is only worth its
    /// complexity to a surface that renders one or delivers the other.
    /// </remarks>
    public async Task<Result<AssistantTurn>> AskAsync(
        AssistantAsk ask, AssistantStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.Watched)
            return await AskStreamingAsync(ask, stream, ct).ConfigureAwait(false);

        if (!IsConfigured)
            return Result.Failure<AssistantTurn>("No assistant is configured on this host.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
            {
                Content = JsonContent.Create(TurnBody.For(ask), options: Json),
            };

            // AutoAct is false and not configurable: this surface stages every action behind a button
            // a human clicks. Auto-running is an admin's deliberate per-turn choice on a surface that
            // offers it, and Discord does not — a message that silently restarted a server would be
            // indistinguishable from one that asked about it.
            _relay.Write(
                request,
                new RelayPrincipal(ask.UserId, ask.DisplayName, ask.Tier),
                new RelayCall(AutoAct: false, ConversationId: ask.ConversationId, Room: ask.Room));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result.Failure<AssistantTurn>(await DescribeFailureAsync(response, ct).ConfigureAwait(false));

            var turn = await response.Content
                .ReadFromJsonAsync<TurnResponseBody>(Json, ct).ConfigureAwait(false);

            if (turn is null)
                return Result.Failure<AssistantTurn>("The assistant answered with nothing I could read.");

            return Result.Success(new AssistantTurn(
                turn.Text ?? string.Empty,
                turn.Confirmations?.Select(Stage).ToArray() ?? []));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // A cancellation nobody asked for is this client's own timeout expiring.
            _logger.LogWarning("Assistant turn timed out after {Timeout}", _http.Timeout);
            return Result.Failure<AssistantTurn>("The assistant took too long to answer.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Assistant turn failed to reach {BaseAddress}", _http.BaseAddress);
            return Result.Failure<AssistantTurn>("I couldn't reach the assistant.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Assistant turn returned a body this bot could not read");
            return Result.Failure<AssistantTurn>("The assistant answered with something I couldn't read.");
        }
    }

    public async Task<Result<string>> RunCommandAsync(
        string command, AssistantAsk ask, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (!IsConfigured)
            return Result.Failure<string>("No assistant is configured on this host.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/commands/{command}")
            {
                Content = JsonContent.Create(new CommandBody(ask.ConversationId), options: Json),
            };

            // The same headers a turn carries, so the command lands on the conversation the next
            // question will continue. A room named here is the whole point: it is what makes clearing
            // a channel's conversation reach that channel's conversation and not the asker's own.
            _relay.Write(
                request,
                new RelayPrincipal(ask.UserId, ask.DisplayName, ask.Tier),
                new RelayCall(AutoAct: false, ConversationId: ask.ConversationId, Room: ask.Room));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result.Failure<string>(await DescribeFailureAsync(response, ct).ConfigureAwait(false));

            var result = await response.Content
                .ReadFromJsonAsync<CommandResultBody>(Json, ct).ConfigureAwait(false);

            // The assistant's own wording, not this bot's. What /new does differs between a private
            // chat and a shared room, and only the assistant knows which one it just did.
            return string.IsNullOrWhiteSpace(result?.Message)
                ? Result.Success("Done.")
                : Result.Success(result.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Assistant command /{Command} timed out after {Timeout}", command, _http.Timeout);
            return Result.Failure<string>("The assistant took too long to answer.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Assistant command /{Command} failed to reach {Base}", command, _http.BaseAddress);
            return Result.Failure<string>("I couldn't reach the assistant.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Assistant command /{Command} returned an unreadable body", command);
            return Result.Failure<string>("The assistant answered with something I couldn't read.");
        }
    }

    /// <summary>
    /// Runs the turn over the frame stream, reporting each step as it arrives and assembling the same
    /// answer the buffered contract would have returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stream is HTTP 200 from its first byte, so a failure arrives <em>in band</em> as an
    /// <c>error</c> frame rather than as a status code. A reader that only checks the response code
    /// reports a turn that failed as one that answered with nothing.
    /// </para>
    /// <para>
    /// A turn that ends without a <c>done</c> frame is reported as a failure and never as an empty
    /// answer: the connection dropping mid-turn is not the assistant saying it had nothing to say.
    /// </para>
    /// </remarks>
    private async Task<Result<AssistantTurn>> AskStreamingAsync(
        AssistantAsk ask, AssistantStream stream, CancellationToken ct)
    {
        if (!IsConfigured)
            return Result.Failure<AssistantTurn>("No assistant is configured on this host.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
            {
                Content = JsonContent.Create(TurnBody.For(ask), options: Json),
            };
            request.Headers.Accept.ParseAdd("text/event-stream");

            _relay.Write(
                request,
                new RelayPrincipal(ask.UserId, ask.DisplayName, ask.Tier),
                new RelayCall(AutoAct: false, ConversationId: ask.ConversationId, Room: ask.Room));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // A conversation already running a turn is told so rather than retried: two investigations
            // of one incident racing each other is worse than one arriving late.
            if (response.StatusCode == HttpStatusCode.Conflict)
                return Result.Failure<AssistantTurn>("The assistant is already working on this conversation.");

            if (!response.IsSuccessStatusCode)
                return Result.Failure<AssistantTurn>(await DescribeFailureAsync(response, ct).ConfigureAwait(false));

            await using var frames = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await ReadTurnAsync(frames, stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Assistant turn timed out after {Timeout}", _http.Timeout);
            return Result.Failure<AssistantTurn>("The assistant took too long to answer.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Assistant turn failed to reach {BaseAddress}", _http.BaseAddress);
            return Result.Failure<AssistantTurn>("I couldn't reach the assistant.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Assistant stream carried a frame this bot could not read");
            return Result.Failure<AssistantTurn>("The assistant answered with something I couldn't read.");
        }
    }

    /// <summary>
    /// Reads the frame stream to its end, reporting activity and returning the assembled turn.
    /// </summary>
    /// <remarks>
    /// Frames are newline-delimited <c>event:</c>/<c>data:</c> pairs separated by a blank line. Only
    /// the payload is read: the frame carries its own <c>type</c> discriminator, so keying on that
    /// rather than on the SSE event name means one parse for both spellings the writer emits.
    /// </remarks>
    private async Task<Result<AssistantTurn>> ReadTurnAsync(
        Stream frames, AssistantStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(frames);

        // The arguments each in-flight call was made with, so a result can name its subject: the
        // finish frame carries the tool and its output, never what it was asked about.
        var started = new Dictionary<string, (string Tool, string? Subject, string? Label)>(StringComparer.Ordinal);

        string? text = null;
        List<StagedAction> staged = [];
        string? error = null;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0)
                continue;

            using var frame = JsonDocument.Parse(payload);
            var root = frame.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.GetString() is not { Length: > 0 } type)
                continue;

            switch (type)
            {
                case "text.delta":
                {
                    // A slice of the reply, in the order it was written — token fragments, and never
                    // whole sentences. Finding the boundaries belongs to whoever is delivering it,
                    // because what counts as one differs between a message and a spoken answer.
                    if (String(root, "text") is { Length: > 0 } slice)
                        stream.Reply?.Report(slice);
                    break;
                }

                case "tool.start":
                {
                    if (stream.Steps is null) break;

                    var id = String(root, "id") ?? string.Empty;
                    var tool = String(root, "tool") ?? string.Empty;
                    // The assistant sends the step's prose with the step. Its catalog is a file on
                    // its own host, so anything written here would describe a build rather than the
                    // tool that actually ran.
                    var label = AssistantToolVocabulary.Label(tool, String(root, "label"));
                    var subject = AssistantToolVocabulary.SubjectOf(Arguments(root));
                    started[id] = (tool, subject, label);
                    stream.Steps.Report(new AssistantActivity(
                        id, tool, label, subject, null, AssistantActivityState.Running));
                    break;
                }

                case "tool.result":
                {
                    if (stream.Steps is null) break;

                    var id = String(root, "id") ?? string.Empty;
                    var tool = String(root, "tool") ?? string.Empty;
                    started.TryGetValue(id, out var start);
                    // The card is read for what it says ABOUT the result; the frame's `summary` is the
                    // model's grounding text and is deliberately never touched here.
                    var detail = AssistantToolVocabulary.DescribeCard(
                        root.TryGetProperty("result", out var card) ? card : null, start.Subject);
                    var named = tool.Length > 0 ? tool : start.Tool ?? string.Empty;
                    stream.Steps.Report(new AssistantActivity(
                        id,
                        named,
                        // The result frame carries no label of its own; the one the start frame
                        // carried is the same step's, so it is kept rather than re-derived.
                        AssistantToolVocabulary.Label(named, start.Label),
                        start.Subject,
                        detail,
                        AssistantActivityState.Done));
                    break;
                }

                case "command.proposed":
                {
                    if (Staged(root) is { } action)
                        staged.Add(action);
                    break;
                }

                case "done":
                    text = String(root, "text") ?? string.Empty;
                    break;

                case "error":
                    error = String(root, "message") ?? "The assistant failed.";
                    break;
            }
        }

        if (error is not null)
            return Result.Failure<AssistantTurn>(error);

        // No terminal frame: the stream ended before the turn did. Reported as a failure rather than
        // as an empty reply, which would read as the assistant having nothing to say.
        if (text is null)
            return Result.Failure<AssistantTurn>("The assistant stopped before it finished answering.");

        return Result.Success(new AssistantTurn(text, staged));
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyDictionary<string, string?>? Arguments(JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in args.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return map;
    }

    /// <summary>
    /// The staged action a <c>command.proposed</c> frame carries, or null when it names no grant this
    /// surface could redeem.
    /// </summary>
    private static StagedAction? Staged(JsonElement root)
    {
        var token = String(root, "token");
        if (string.IsNullOrEmpty(token))
            return null;

        // The frame names the operation `verb` where the buffered contract calls it `kind`, and puts
        // the target inside a `subject` object where the buffered one has it flat. Both spellings are
        // read so this surface produces the same StagedAction either way — a button built from one
        // shape and a confirmation handler expecting the other is a grant that cannot be redeemed.
        var kind = String(root, "verb") ?? String(root, "kind") ?? string.Empty;
        var target = root.TryGetProperty("subject", out var subject) && subject.ValueKind == JsonValueKind.Object
            ? String(subject, "id") ?? string.Empty
            : String(root, "target") ?? string.Empty;

        return new StagedAction(
            kind, target, String(root, "instanceName"), token,
            String(root, "configKey"), String(root, "configValue"));
    }

    public async Task<Result<AssistantOutcome>> ConfirmAsync(
        AssistantApproval approval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);

        if (!IsConfigured)
            return Result.Failure<AssistantOutcome>("No assistant is configured on this host.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/confirm")
            {
                Content = JsonContent.Create(new ConfirmBody(approval.Token), options: Json),
            };

            // The approver's own identity and their tier as it is right now — the assistant judges the
            // click on that, not on whatever was true when the action was proposed.
            _relay.Write(request, new RelayPrincipal(approval.UserId, approval.DisplayName, approval.Tier));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            // A refused grant is the one failure worth its own words: it means the action is simply
            // no longer on the table, which is not the same as the assistant being unwell.
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogInformation(
                    "Assistant refused a confirmation from {User} — expired, already used, or not theirs",
                    approval.UserId);
                return Result.Failure<AssistantOutcome>(
                    "That confirmation has expired or was already used — ask me again.");
            }

            if (!response.IsSuccessStatusCode)
                return Result.Failure<AssistantOutcome>(await DescribeFailureAsync(response, ct).ConfigureAwait(false));

            var confirmed = await response.Content
                .ReadFromJsonAsync<ConfirmResponseBody>(Json, ct).ConfigureAwait(false);

            if (confirmed is null)
                return Result.Failure<AssistantOutcome>("The assistant answered with nothing I could read.");

            return Result.Success(new AssistantOutcome(
                confirmed.Text ?? string.Empty,
                confirmed.Success,
                confirmed.Outcome?.Verdict,
                confirmed.Outcome?.ObservedState));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The action may well still be running on the other side — say what is known, which is
            // that the answer did not arrive, never that it failed.
            _logger.LogWarning("Assistant confirmation timed out after {Timeout}", _http.Timeout);
            return Result.Failure<AssistantOutcome>(
                "The assistant is taking too long to answer — it may still be working on it.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Assistant confirmation failed to reach {BaseAddress}", _http.BaseAddress);
            return Result.Failure<AssistantOutcome>("I couldn't reach the assistant.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Assistant confirmation returned a body this bot could not read");
            return Result.Failure<AssistantOutcome>("The assistant answered with something I couldn't read.");
        }
    }

    /// <summary>
    /// Turns a refusal into something worth showing a person, keeping the detail in the journal.
    /// </summary>
    /// <remarks>
    /// The distinction that matters to whoever is reading the reply is whether the bot is misconfigured
    /// (nothing they can do) or the assistant is having trouble (worth asking again), so the message
    /// says which. A status code in a Discord channel helps nobody.
    /// </remarks>
    private async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ReadProblemAsync(response, ct).ConfigureAwait(false);
        _logger.LogWarning(
            "Assistant refused the turn: {Status} {Reason} {Detail}",
            (int)response.StatusCode, response.ReasonPhrase, body);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "The assistant didn't accept my credentials — the relay secret on this host needs a look.",
            HttpStatusCode.BadRequest => "The assistant couldn't make sense of that request.",
            _ => "The assistant had trouble answering that.",
        };
    }

    /// <summary>Reads whatever detail the response carries, without letting that read fail the call.</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length <= 500 ? text : text[..500];
        }
        catch (Exception)
        {
            return "(no body)";
        }
    }

    private static StagedAction Stage(ConfirmationBody c) => new(
        c.Kind ?? string.Empty, c.Target ?? string.Empty, c.InstanceName,
        c.Token ?? string.Empty, c.ConfigKey, c.ConfigValue);

    public void Dispose() => _http.Dispose();

    // The wire shapes, named here rather than shared: the assistant's contract package carries the
    // relay headers, which are the part two surfaces must not disagree about. A response body a client
    // reads a few fields out of is not, and taking a package dependency for it would couple this bot's
    // build to every unrelated change in that surface.
    /// <summary>
    /// The question, and the shape the answer is wanted in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>style</c> is omitted for an answer that will be read on a screen rather than sent as
    /// "default". An assistant too old to know the field then receives exactly the body it always did,
    /// which is what makes this a change no deployment has to be sequenced around: the field only ever
    /// appears on the surface that needs it.
    /// </para>
    /// <para>
    /// The wire word is written here and nowhere else. It belongs to the assistant's vocabulary, and
    /// Core says only that an answer is going to be spoken — which is the fact this surface actually
    /// has.
    /// </para>
    /// </remarks>
    private sealed record TurnBody(
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("style")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Style = null)
    {
        private const string SpokenWire = "voice";

        public static TurnBody For(AssistantAsk ask) =>
            new(ask.Prompt, ask.Spoken ? SpokenWire : null);
    }

    private sealed record TurnResponseBody(string? Text, IReadOnlyList<ConfirmationBody>? Confirmations);

    /// <summary>
    /// What a chat command is asked against. The conversation id is sent for the same reason a turn
    /// sends it — a room in the headers wins over it, and the assistant is what decides that.
    /// </summary>
    private sealed record CommandBody(
        [property: JsonPropertyName("conversationId")] string? ConversationId);

    private sealed record CommandResultBody(string? Command, string? Message);

    private sealed record ConfirmationBody(
        string? Kind, string? Target, string? InstanceName, string? Token,
        string? ConfigKey, string? ConfigValue);

    private sealed record ConfirmBody([property: JsonPropertyName("token")] string Token);

    private sealed record ConfirmResponseBody(string? Text, bool Success, OutcomeBody? Outcome);

    private sealed record OutcomeBody(string? Verdict, string? ObservedState);
}
