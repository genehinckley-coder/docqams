using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;

namespace DocQaMs.Claude.Tests.Corpus;

/// <summary>
/// Plan item 3, offline: the production token counter sends a first question's request, and the
/// fingerprint covers exactly the document blocks the SDK sends.
/// </summary>
public sealed class ClaudeTokenCounterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCount_IsOfAFirstQuestion_WithTheSystemPromptAndAPlaceholder()
    {
        var stub = new StubHandler(_ => StubHandler.Json("""{"input_tokens":1234}"""));
        using ClaudeChatClientProvider provider = CreateProvider(stub);

        long tokens = await new ClaudeTokenCounter(() => provider).CountAsync(ModelIds.ClaudeSonnet5, SpikeFixtures.Documents, Ct);

        Assert.Equal(1234, tokens);
        CapturedRequest request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("api.anthropic.com", request.Uri?.Host);
        Assert.Equal("/v1/messages/count_tokens", request.Uri?.AbsolutePath);
        Assert.Equal(ClaudeTestClient.FakeKey, request.ApiKey);
        Assert.False(request.HasAuthorization);

        JsonObject body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal(["messages", "model", "system"], body.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ModelIds.ClaudeSonnet5, (string?)body["model"]);

        // A plain string, as the chat client sends it: the count covers the same prefix.
        Assert.Equal(CacheAffecting.SystemPrompt, (string?)body["system"]);
        JsonObject message = Assert.IsType<JsonObject>(Assert.Single(body["messages"]!.AsArray()));
        Assert.Equal("user", (string?)message["role"]);
        JsonArray content = message["content"]!.AsArray();
        Assert.Equal(SpikeFixtures.Documents.Count + 1, content.Count);
        JsonTest.Equal($$"""{"type":"text","text":"{{ClaudeTokenCounter.PlaceholderQuestion}}"}""", content[^1]);
    }

    [Fact]
    public async Task TheFingerprintedBlocks_AreExactlyTheBlocksTheSdkSends()
    {
        // The fingerprint is written field by field, not by the SDK. If MessageRequestBuilder or the Claude
        // mapper ever sends a field the fingerprint does not cover, or renders one differently, this fails.
        var stub = new StubHandler(_ => StubHandler.Json("""{"input_tokens":1}"""));
        using ClaudeChatClientProvider provider = CreateProvider(stub);

        await new ClaudeTokenCounter(() => provider).CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, Ct);

        JsonArray content = JsonNode.Parse(Assert.Single(stub.Requests).Body!)!["messages"]![0]!["content"]!.AsArray();
        JsonArray sentBlocks = [.. content.Take(content.Count - 1).Select(block => block!.DeepClone())];
        JsonTest.Equal(Encoding.UTF8.GetString(CorpusFingerprint.CanonicalJson(SpikeFixtures.Documents)), sentBlocks);
    }

    [Fact]
    public async Task TheCountedBlocks_AreTheBlocksAFirstQuestionSends()
    {
        // The count is only a guard if it measures what an answer actually sends.
        var stub = new StubHandler(number => number == 1 ? StubHandler.Json("""{"input_tokens":1}""") : Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = CreateProvider(stub);

        await new ClaudeTokenCounter(() => provider).CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, Ct);
        await ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct);

        JsonNode counted = JsonNode.Parse(stub.Requests[0].Body!)!;
        JsonNode sent = JsonNode.Parse(stub.Requests[1].Body!)!;
        Assert.Equal((string?)sent["system"], (string?)counted["system"]);
        JsonArray countedContent = counted["messages"]![0]!["content"]!.AsArray();
        JsonArray sentContent = sent["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(sentContent.Count, countedContent.Count);
        for (int i = 0; i < sentContent.Count - 1; i++)
        {
            Assert.True(JsonNode.DeepEquals(sentContent[i], countedContent[i]), $"Block {i} differs.");
        }
    }

    [Fact]
    public async Task TheClient_IsObtainedWhenACountIsMade_NotBefore()
    {
        // A missing key file must surface as a failed count, which the UI can show and retry.
        int calls = 0;
        var counter = new ClaudeTokenCounter(() =>
        {
            calls++;
            throw new FileNotFoundException("API key file not found.");
        });
        Assert.Equal(0, calls);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => counter.CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, Ct));
        Assert.Equal(1, calls);
    }

    public static TheoryData<int, string, ChatProviderFailure, string> StatusFailures => new()
    {
        { 401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", ChatProviderFailure.Authentication, "authentication_error" },
        { 429, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", ChatProviderFailure.RateLimited, "rate_limit_error" },
        { 400, """{"type":"error","error":{"type":"invalid_request_error","message":"model: not found"}}""", ChatProviderFailure.ApiError, "invalid_request_error" },
    };

    [Theory]
    [MemberData(nameof(StatusFailures))]
    public async Task AFailedCount_IsTranslated_WithTheApisOwnMessage(int status, string body, ChatProviderFailure failure, string errorType)
    {
        var stub = new StubHandler(_ =>
        {
            HttpResponseMessage response = StubHandler.Json(body, (HttpStatusCode)status);
            response.Headers.Add("retry-after-ms", "0");
            return response;
        });
        using ClaudeChatClientProvider provider = CreateProvider(stub);

        var ex = await Assert.ThrowsAsync<ChatProviderException>(
            () => new ClaudeTokenCounter(() => provider).CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, Ct));

        Assert.Equal(failure, ex.Failure);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(errorType, ex.ErrorType);
        Assert.NotNull(ex.InnerException);
        Assert.DoesNotContain("kkkk", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACountThatCannotConnect_IsAConnectionFailure()
    {
        var stub = new StubHandler(_ => throw new HttpRequestException("Connection refused (simulated)."));
        using ClaudeChatClientProvider provider = CreateProvider(stub);

        var ex = await Assert.ThrowsAsync<ChatProviderException>(
            () => new ClaudeTokenCounter(() => provider).CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, Ct));

        Assert.Equal(ChatProviderFailure.Connection, ex.Failure);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task ACancelledCount_IsNotTranslated()
    {
        using ClaudeChatClientProvider provider = CreateProvider(new StubHandler(_ => StubHandler.Json("""{"input_tokens":1}""")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Exception? ex = await Record.ExceptionAsync(
            () => new ClaudeTokenCounter(() => provider).CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, cancelled.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task AMissingModel_IsRefusedBeforeAnyCall()
    {
        var stub = new StubHandler(_ => StubHandler.Json("""{"input_tokens":1}"""));
        using ClaudeChatClientProvider provider = CreateProvider(stub);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => new ClaudeTokenCounter(() => provider).CountAsync("", SpikeFixtures.Documents, Ct));

        Assert.Empty(stub.Requests);
    }

    private static ClaudeChatClientProvider CreateProvider(StubHandler stub) =>
        ClaudeChatClientProvider.Create(ClaudeTestClient.FakeKey, TestSettings.ValidOptions().RequestTimeouts, [stub]);
}
