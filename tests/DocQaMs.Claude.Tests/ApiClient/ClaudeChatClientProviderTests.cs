using System.Collections;
using System.Net;
using Anthropic;
using Anthropic.Models.Models;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Claude.Tests.ApiClient;

/// <summary>
/// Offline: every request is answered by <see cref="StubHandler"/>. The live checks of the request
/// shape are plan item 1's.
/// </summary>
/// <remarks>
/// In a non-parallel collection, because one test sets process-wide ANTHROPIC_* environment
/// variables that every client construction could read.
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ClaudeChatClientProviderTests
{
    // Every ANTHROPIC_* variable named in the SDK 12.47.0 assembly.
    private static readonly string[] AnthropicVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_PROFILE",
        "ANTHROPIC_WORKSPACE_ID", "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_IDENTITY_TOKEN",
        "ANTHROPIC_IDENTITY_TOKEN_FILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_SERVICE_ACCOUNT_ID",
        "ANTHROPIC_WEBHOOK_SIGNING_KEY",
    ];

    private const string Key = ClaudeTestClient.FakeKey;

    private const string ModelJson =
        """{"type":"model","id":"claude-opus-5","display_name":"Claude Opus 5","created_at":"2026-01-01T00:00:00Z"}""";

    private static readonly Dictionary<EffortLevel, TimeSpan> Timeouts = new()
    {
        [EffortLevel.Low] = TimeSpan.FromMinutes(20),
        [EffortLevel.Medium] = TimeSpan.FromMinutes(21),
        [EffortLevel.High] = TimeSpan.FromMinutes(22),
        [EffortLevel.XHigh] = TimeSpan.FromMinutes(30),
        [EffortLevel.Max] = TimeSpan.FromMinutes(60),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryEffortLevel_HasItsOwnTimeout_AndSendsThroughTheSharedHandlers()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(Key, Timeouts, [stub]);

        foreach (EffortLevel level in CacheAffecting.EffortLevels)
        {
            IChatClient chatClient = provider.ForEffort(level);
            IAnthropicClient view = Assert.IsAssignableFrom<IAnthropicClient>(chatClient.GetService<IAnthropicClient>());
            Assert.Equal(Timeouts[level], view.Timeout);
            Assert.Same(chatClient, provider.ForEffort(level));
            await ClaudeTestClient.StreamAsync(chatClient, ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()], level), Ct);
        }

        // Building the views must not have changed the root client's timeout.
        Assert.Null(provider.AnthropicClient.Timeout);
        Assert.Equal(CacheAffecting.EffortLevels.Count, stub.Requests.Count);
        Assert.All(stub.Requests, request => Assert.Equal("/v1/messages", request.Uri?.AbsolutePath));
    }

    [Fact]
    public void EachChatClient_IsTheClaudeMiddleware_OverTelemetry_OverTheSdksAdapter()
    {
        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(Key, Timeouts, []);

        IChatClient chatClient = provider.ForEffort(EffortLevel.High);

        // Outermost, so the telemetry sees the adapter's single usage rather than the middleware's two.
        Assert.IsType<ClaudeChatClient>(chatClient);
        OpenTelemetryChatClient telemetry = Assert.IsType<OpenTelemetryChatClient>(chatClient.GetService<OpenTelemetryChatClient>());
        Assert.Null(telemetry.GetService<ClaudeChatClient>());
    }

    [Fact]
    public async Task Requests_GoToTheFixedHost_WithTheKeyAsXApiKey_AndNoAuthorizationHeader()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(Key, Timeouts, [stub]);

        await ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct);

        CapturedRequest request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https", request.Uri?.Scheme);
        Assert.Equal("api.anthropic.com", request.Uri?.Host);
        Assert.Equal(Key, request.ApiKey);
        Assert.False(request.HasAuthorization);
    }

    [Fact]
    public async Task AmbientAnthropicVariables_ChangeNeitherTheHostNorTheCredentialsNorTheHeaders()
    {
        Dictionary<string, string?> saved = AnthropicVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string name in AnthropicVariables)
            {
                Environment.SetEnvironmentVariable(name, name == "ANTHROPIC_BASE_URL" ? "https://junk.invalid" : "junk-value");
            }

            var stub = new StubHandler(number => number == 1 ? StubHandler.Json(ModelJson) : Sse.Response(CannedAnswer.Events));
            using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(Key, Timeouts, [stub]);

            await Retrieve(provider.AnthropicClient);
            await ClaudeTestClient.StreamAsync(
                provider.ForEffort(EffortLevel.Max),
                ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()], EffortLevel.Max),
                Ct);

            Assert.Equal(2, stub.Requests.Count);
            Assert.All(stub.Requests, request =>
            {
                Assert.Equal("api.anthropic.com", request.Uri?.Host);
                Assert.Equal(Key, request.ApiKey);

                // The SDK still sends an ambient ANTHROPIC_AUTH_TOKEN as a bearer token; the provider removes it.
                Assert.False(request.HasAuthorization);
                Assert.DoesNotContain(request.Headers, header => header.Value.Contains("junk", StringComparison.Ordinal));
            });
        }
        finally
        {
            foreach ((string name, string? value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void Creating_TheClient_LeavesTheEnvironmentUntouched()
    {
        // Putting the key in an environment variable would hand it to every child process.
        Dictionary<string, string?> before = Snapshot();

        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(
            Key, Timeouts, [new StubHandler(_ => StubHandler.Json(ModelJson))]);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ARetryableFailure_IsLoggedWithoutTheKey_AndRetried()
    {
        var logger = new ListLogger<RetryLoggingHandler>();
        var stub = new StubHandler(number => number == 1
            ? StubHandler.Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json(ModelJson));
        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(
            Key, Timeouts, [new RetryLoggingHandler(logger), stub]);

        await Retrieve(provider.AnthropicClient);

        Assert.Equal(2, stub.Requests.Count);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("503", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kkkk", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverloadedAnswerRequest_IsLoggedAndRetried_BeforeItsStreamStarts()
    {
        var logger = new ListLogger<RetryLoggingHandler>();
        var stub = new StubHandler(number => number == 1
            ? WithoutRetryDelay(StubHandler.Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", (HttpStatusCode)529))
            : Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(
            Key, Timeouts, [new RetryLoggingHandler(logger), stub]);

        List<ChatResponseUpdate> updates = await ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct);

        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains(updates, update => update.FinishReason == ChatFinishReason.Stop);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("529", entry.Message, StringComparison.Ordinal);
        Assert.Contains("/v1/messages", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulRequest_LogsNothing()
    {
        var logger = new ListLogger<RetryLoggingHandler>();
        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(
            Key, Timeouts, [new RetryLoggingHandler(logger), new StubHandler(_ => StubHandler.Json(ModelJson))]);

        await Retrieve(provider.AnthropicClient);

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(409, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void RetryableStatuses_FollowTheSdksDocumentation(int status, bool retryable) =>
        Assert.Equal(retryable, RetryLoggingHandler.IsRetryableStatus(status));

    [Fact]
    public void AMissingTimeout_IsRefused()
    {
        Dictionary<EffortLevel, TimeSpan> timeouts = new(Timeouts);
        timeouts.Remove(EffortLevel.XHigh);

        var ex = Assert.Throws<ArgumentException>(() => ClaudeChatClientProvider.Create(Key, timeouts, []));

        Assert.Contains("XHigh", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankKey_IsRefused(string key) =>
        Assert.ThrowsAny<ArgumentException>(() => ClaudeChatClientProvider.Create(key, Timeouts, []));

    [Fact]
    public void ForEffort_RejectsAnUndefinedLevel()
    {
        using ClaudeChatClientProvider provider = ClaudeChatClientProvider.Create(Key, Timeouts, []);

        Assert.Throws<ArgumentOutOfRangeException>(() => provider.ForEffort((EffortLevel)99));
    }

    private static Task<ModelInfo> Retrieve(IAnthropicClient client) =>
        client.Models.Retrieve(ModelIds.ClaudeOpus5, new ModelRetrieveParams(), Ct);

    /// <summary>The SDK retries 429 and 5xx twice; a zero retry-after keeps the test fast.</summary>
    private static HttpResponseMessage WithoutRetryDelay(HttpResponseMessage response)
    {
        response.Headers.Add("retry-after-ms", "0");
        return response;
    }

    private static Dictionary<string, string?> Snapshot() =>
        Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
}
