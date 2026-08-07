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
/// The buffered reply is taken rather than the token stream: Discord has no surface that streams
/// text, and one message posted when the answer is ready is what it can actually render.
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

    public async Task<Result<AssistantTurn>> AskAsync(AssistantAsk ask, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (!IsConfigured)
            return Result.Failure<AssistantTurn>("No assistant is configured on this host.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
            {
                Content = JsonContent.Create(new TurnBody(ask.Prompt), options: Json),
            };

            // AutoAct is false and not configurable: this surface stages every action behind a button
            // a human clicks. Auto-running is an admin's deliberate per-turn choice on a surface that
            // offers it, and Discord does not — a message that silently restarted a server would be
            // indistinguishable from one that asked about it.
            _relay.Write(
                request,
                new RelayPrincipal(ask.UserId, ask.DisplayName, ask.Tier),
                new RelayCall(AutoAct: false, ConversationId: ask.ConversationId));

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
    private sealed record TurnBody([property: JsonPropertyName("prompt")] string Prompt);

    private sealed record TurnResponseBody(string? Text, IReadOnlyList<ConfirmationBody>? Confirmations);

    private sealed record ConfirmationBody(
        string? Kind, string? Target, string? InstanceName, string? Token,
        string? ConfigKey, string? ConfigValue);
}
