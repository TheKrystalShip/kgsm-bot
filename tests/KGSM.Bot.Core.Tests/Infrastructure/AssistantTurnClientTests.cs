using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Assistant;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Auth;

using Xunit;

namespace KGSM.Bot.Tests.Infrastructure;

/// <summary>
/// What the bot puts on the wire when it asks the assistant something on a person's behalf, and what
/// it does with each answer it can get back.
/// </summary>
/// <remarks>
/// The header assertions name the literal wire spellings rather than the relay package's constants:
/// the assistant reads those literals, so a test written against the constants would agree with a
/// rename on both sides while meaning something new to the service that has not been rebuilt.
/// </remarks>
public class AssistantTurnClientTests
{
    private const string Secret = "host-relay-secret";

    private static readonly AssistantAsk Ask = new(
        UserId: "385730677141929985",
        DisplayName: "Heisen",
        Tier: KgsmTier.Operator,
        ConversationId: "911747779704008745",
        Prompt: "is factorio running?");

    /// <summary>A transport that answers with a canned response and keeps what it was sent.</summary>
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        public string? SeenBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            SeenBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Transport Answering(string body) => new(_ => Json(HttpStatusCode.OK, body));

    private static AssistantTurnClient Client(
        Transport transport, string baseUrl = "http://127.0.0.1:5180", string secret = Secret) =>
        new(new AssistantOptions { BaseUrl = baseUrl, RelaySecret = secret, TimeoutSeconds = 30 },
            transport, NullLogger<AssistantTurnClient>.Instance);

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task ForwardsTheAskingPerson_TheirTier_AndThisLeafsName()
    {
        var transport = Answering("""{"text":"It is running.","confirmations":[]}""");
        using var client = Client(transport);

        await client.AskAsync(Ask);

        var request = transport.Seen!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/turn");
        Header(request, "X-Relay-Secret").Should().Be(Secret);
        Header(request, "X-Relay-User").Should().Be("385730677141929985");
        Header(request, "X-Relay-User-Name").Should().Be("Heisen");
        Header(request, "X-Relay-Tier").Should().Be(KgsmTiers.ToWire(KgsmTier.Operator));
        Header(request, "X-Relay-Leaf").Should().Be("kgsm-bot");
    }

    /// <summary>
    /// The channel the message was posted in is the conversation, so a thread in one channel is a
    /// separate context window from one in another — and the same thread the person's other surfaces
    /// list, because it sub-scopes their own memory rather than naming a namespace of its own.
    /// </summary>
    [Fact]
    public async Task TheChannelIsTheConversationScope()
    {
        var transport = Answering("""{"text":"ok","confirmations":[]}""");
        using var client = Client(transport);

        await client.AskAsync(Ask);

        Header(transport.Seen!, "X-Relay-Conversation-Id").Should().Be("911747779704008745");
    }

    /// <summary>
    /// This surface never asks for auto-run, whoever is asking. A message that silently restarted a
    /// server would be indistinguishable from one that asked about it, so every action is staged.
    /// </summary>
    [Fact]
    public async Task NeverAsksToRunAnActionWithoutConfirmation()
    {
        var transport = Answering("""{"text":"ok","confirmations":[]}""");
        using var client = Client(transport);

        await client.AskAsync(Ask with { Tier = KgsmTier.Admin });

        Header(transport.Seen!, "X-Relay-Auto-Act").Should().Be("false");
    }

    [Fact]
    public async Task SendsThePromptAsTheBody()
    {
        var transport = Answering("""{"text":"ok","confirmations":[]}""");
        using var client = Client(transport);

        await client.AskAsync(Ask);

        using var body = JsonDocument.Parse(transport.SeenBody!);
        body.RootElement.GetProperty("prompt").GetString().Should().Be("is factorio running?");
    }

    [Fact]
    public async Task ReadsTheReplyAndEveryStagedAction()
    {
        var transport = Answering("""
            {
              "text": "That will stop the server.",
              "confirmations": [
                { "kind": "stop", "target": "factorio", "instanceName": null, "token": "tok-1" },
                { "kind": "setconfig", "target": "minecraft", "instanceName": null, "token": "tok-2",
                  "configKey": "executable_arguments", "configValue": "-Xmx4G" }
              ],
              "usage": { "promptTokens": 10, "responseTokens": 4, "usedTokens": 14,
                         "contextWindow": 32768, "remainingTokens": 32754 }
            }
            """);
        using var client = Client(transport);

        var result = await client.AskAsync(Ask);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Text.Should().Be("That will stop the server.");
        result.Value.StagedActions.Should().HaveCount(2);
        result.Value.StagedActions[0].Should().BeEquivalentTo(
            new StagedAction("stop", "factorio", null, "tok-1"));
        result.Value.StagedActions[1].Should().BeEquivalentTo(
            new StagedAction("setconfig", "minecraft", null, "tok-2", "executable_arguments", "-Xmx4G"));
    }

    /// <summary>
    /// An answer with nothing staged is the ordinary case, and must not be reported as a failure or
    /// carry a null the caller has to guard.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithNoStagedActionsCarriesAnEmptyList()
    {
        var transport = Answering("""{"text":"Nothing to do."}""");
        using var client = Client(transport);

        var result = await client.AskAsync(Ask);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StagedActions.Should().BeEmpty();
    }

    /// <summary>
    /// An unreachable assistant is reported to the person who asked — never papered over. The bot
    /// holds no second engine to answer from, and pretending otherwise is what splits one person's
    /// history in two.
    /// </summary>
    [Fact]
    public async Task AnUnreachableAssistantFailsWithSomethingWorthShowing()
    {
        var transport = new Transport(_ => throw new HttpRequestException("connection refused"));
        using var client = Client(transport);

        var result = await client.AskAsync(Ask);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("I couldn't reach the assistant.");
    }

    /// <summary>
    /// A rejected secret is a misconfiguration of this host, not something the asker did — and the
    /// two need different messages, because only one of them is worth trying again.
    /// </summary>
    [Fact]
    public async Task RefusedCredentialsSayWhatIsActuallyWrong()
    {
        var transport = new Transport(_ => Json(HttpStatusCode.Unauthorized, """{"error":"nope"}"""));
        using var client = Client(transport);

        var result = await client.AskAsync(Ask);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("relay secret");
    }

    [Fact]
    public async Task AnUpstreamFailureIsReportedWithoutTheStatusCode()
    {
        var transport = new Transport(_ => Json(HttpStatusCode.BadGateway, """{"detail":"ollama down"}"""));
        using var client = Client(transport);

        var result = await client.AskAsync(Ask);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("The assistant had trouble answering that.");
    }

    /// <summary>
    /// No address and no secret both leave the surface off. Half-configured is the case worth pinning:
    /// an address with no secret would produce questions the assistant refuses one at a time, where
    /// reporting the surface unconfigured says so once, at startup.
    /// </summary>
    [Theory]
    [InlineData("", Secret)]
    [InlineData("http://127.0.0.1:5180", "")]
    [InlineData("not-a-url", Secret)]
    public async Task WithoutBothAnAddressAndASecret_TheSurfaceIsOff(string baseUrl, string secret)
    {
        var transport = new Transport(_ => throw new InvalidOperationException("must not be called"));
        using var client = Client(transport, baseUrl, secret);

        client.IsConfigured.Should().BeFalse();
        (await client.IsAvailableAsync()).Should().BeFalse();
        (await client.AskAsync(Ask)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AvailabilityIsWhetherHealthAnswers()
    {
        var ok = new Transport(_ => Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        using var up = Client(ok);
        (await up.IsAvailableAsync()).Should().BeTrue();
        ok.Seen!.RequestUri!.AbsolutePath.Should().Be("/health");

        using var down = Client(new Transport(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        (await down.IsAvailableAsync()).Should().BeFalse();
    }

    // ---- approving a staged action -------------------------------------------------------------

    private static readonly AssistantApproval Approval = new(
        UserId: "385730677141929985", DisplayName: "Heisen", Tier: KgsmTier.Operator,
        Token: "60c24e5b21a7c863fae9648b996ae116");

    /// <summary>
    /// The click is forwarded as the clicker, with the tier they hold at that moment — the assistant
    /// judges the approval on that, not on whatever was true when the action was proposed.
    /// </summary>
    [Fact]
    public async Task AnApprovalForwardsTheClicker_AndHandsBackTheGrantUntouched()
    {
        var transport = Answering("""{"text":"'Ketchup' has been started.","success":true}""");
        using var client = Client(transport);

        await client.ConfirmAsync(Approval);

        var request = transport.Seen!;
        request.RequestUri!.AbsolutePath.Should().Be("/confirm");
        Header(request, "X-Relay-User").Should().Be("385730677141929985");
        Header(request, "X-Relay-Tier").Should().Be(KgsmTiers.ToWire(KgsmTier.Operator));
        Header(request, "X-Relay-Leaf").Should().Be("kgsm-bot");

        using var body = JsonDocument.Parse(transport.SeenBody!);
        body.RootElement.GetProperty("token").GetString().Should().Be(Approval.Token);
    }

    [Fact]
    public async Task AnApprovalReportsTheWatchedVerdict()
    {
        var transport = Answering("""
            {"text":"'Ketchup' has been started.","success":true,
             "outcome":{"verdict":"settled","verb":"start","instance":"Ketchup","observedState":"running"}}
            """);
        using var client = Client(transport);

        var result = await client.ConfirmAsync(Approval);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Success.Should().BeTrue();
        result.Value.Verdict.Should().Be("settled");
        result.Value.ObservedState.Should().Be("running");
        result.Value.Text.Should().Contain("has been started");
    }

    /// <summary>
    /// An outcome with no observation carries none. "I could not read its state" and "it is not
    /// running" are different facts, and only one of them was measured.
    /// </summary>
    [Fact]
    public async Task AnOutcomeWithNothingObservedClaimsNothing()
    {
        var transport = Answering("""{"text":"Accepted.","success":false}""");
        using var client = Client(transport);

        var result = await client.ConfirmAsync(Approval);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Success.Should().BeFalse();
        result.Value.Verdict.Should().BeNull();
        result.Value.ObservedState.Should().BeNull();
    }

    /// <summary>
    /// A refused grant — expired, already redeemed, or somebody else's — is its own message: the
    /// action is no longer on the table, which is not the assistant being unwell.
    /// </summary>
    [Fact]
    public async Task ARefusedGrantSaysTheActionIsGone_NotThatTheAssistantFailed()
    {
        var transport = new Transport(_ => Json(
            HttpStatusCode.BadRequest, """{"error":"Invalid or expired confirmation."}"""));
        using var client = Client(transport);

        var result = await client.ConfirmAsync(Approval);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("expired or was already used");
        result.Error.Should().NotContain("trouble answering");
    }

    /// <summary>
    /// A timeout is not a failure of the action — it may well still be running on the other side, so
    /// the message says what is known rather than asserting an outcome nobody measured.
    /// </summary>
    [Fact]
    public async Task AnUnreachableAssistantDoesNotClaimTheActionFailed()
    {
        var transport = new Transport(_ => throw new HttpRequestException("connection refused"));
        using var client = Client(transport);

        var result = await client.ConfirmAsync(Approval);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("I couldn't reach the assistant.");
    }

    [Fact]
    public async Task WithNoAssistantConfigured_AnApprovalIsRefusedWithoutCallingAnything()
    {
        var transport = new Transport(_ => throw new InvalidOperationException("must not be called"));
        using var client = Client(transport, baseUrl: "");

        (await client.ConfirmAsync(Approval)).IsFailure.Should().BeTrue();
    }
}
