using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Claude.Tests.Claude;

/// <summary>
/// The streaming contract of <c>docs/MIGRATION.md</c>, as <see cref="ClaudeChatClient"/> produces it over the
/// SDK's real adapter from canned server-sent events: block indexes, citations, redacted reasoning, usage at
/// the start and the end, the stop reason, and every way a stream can fail.
/// </summary>
public sealed class ClaudeChatClientStreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryReasoningAndTextContent_CarriesItsBlockIndex()
    {
        List<ChatResponseUpdate> updates = await StreamAsync(CannedAnswer.Events);

        List<AIContent> blockContents = [.. ClaudeTestClient.Contents(updates).Where(content => content is TextContent or TextReasoningContent)];
        Assert.NotEmpty(blockContents);
        Assert.All(blockContents, content => Assert.NotNull(ClaudeTestClient.BlockIndex(content)));

        // Deltas of one block share its index; the indexes never go back.
        Assert.Equal(
            [0L, 1L, 2L, 3L, 4L, 5L],
            blockContents.Select(content => ClaudeTestClient.BlockIndex(content)!.Value).Distinct());
        Assert.All(blockContents.Where(content => ClaudeTestClient.BlockIndex(content) is 0 or 1), content => Assert.IsType<TextReasoningContent>(content));
        Assert.All(blockContents.Where(content => ClaudeTestClient.BlockIndex(content) >= 2), content => Assert.IsType<TextContent>(content));
    }

    [Fact]
    public async Task Reasoning_StreamsItsTextAndSignature()
    {
        List<ChatResponseUpdate> updates = await StreamAsync(CannedAnswer.Events);

        List<TextReasoningContent> reasoning = [.. ClaudeTestClient.Contents(updates).OfType<TextReasoningContent>().Where(content => ClaudeTestClient.BlockIndex(content) == 0)];
        Assert.Equal(CannedAnswer.Thinking, string.Concat(reasoning.Select(content => content.Text)));
        Assert.Contains(reasoning, content => content.ProtectedData == CannedAnswer.Signature);
        Assert.All(reasoning, content => Assert.False(IsRedacted(content)));
    }

    [Fact]
    public async Task RedactedReasoning_CarriesTheNeutralMarkerAndItsData_NotTheAdaptersMarker()
    {
        List<ChatResponseUpdate> updates = await StreamAsync(CannedAnswer.Events);

        TextReasoningContent redacted = Assert.Single(
            ClaudeTestClient.Contents(updates).OfType<TextReasoningContent>(), content => ClaudeTestClient.BlockIndex(content) == 1);
        Assert.True(IsRedacted(redacted));
        Assert.Equal("cmVkYWN0ZWQ=", redacted.ProtectedData);
        Assert.True(string.IsNullOrEmpty(redacted.Text));
        Assert.False(redacted.AdditionalProperties!.ContainsKey(ClaudeStreamTranslator.AdapterRedactedThinkingKey));
    }

    [Fact]
    public async Task EachCitation_ArrivesAsAnEmptyTextWithOneAnnotation_CarryingItsOffsets()
    {
        List<ChatResponseUpdate> updates = await StreamAsync(CannedAnswer.Events);

        List<TextContent> cited = [.. ClaudeTestClient.Contents(updates).OfType<TextContent>().Where(content => content.Annotations is { Count: > 0 })];
        Assert.Collection(
            cited,
            content => AssertCitation(content, 3, CannedAnswer.CitedText1, CannedAnswer.DocumentTitle, 0, 851, 880),
            content => AssertCitation(content, 3, CannedAnswer.CitedText2, CannedAnswer.DocumentTitle, 0, 950, 970));

        // The block's text arrives on its own, as it streamed.
        Assert.Equal(
            "a minimum of 5 gallons",
            string.Concat(ClaudeTestClient.Contents(updates).OfType<TextContent>().Where(content => ClaudeTestClient.BlockIndex(content) == 3).Select(content => content.Text)));
    }

    [Fact]
    public async Task ACitationWithANullDocumentTitle_IsStillRead()
    {
        // The SDK (12.47.0) did not recognize such a citation, which the API allows.
        List<string> events = StreamEvents.Answer(StreamEvents.Text(0, "Untitled.", StreamEvents.Citation(3, 9, "quoted", documentIndex: 1, title: null)));

        List<ChatResponseUpdate> updates = await StreamAsync(events);

        TextContent cited = Assert.Single(ClaudeTestClient.Contents(updates).OfType<TextContent>(), content => content.Annotations is { Count: > 0 });
        AssertCitation(cited, 0, "quoted", null, 1, 3, 9);
    }

    [Fact]
    public async Task CitationsPresentAtTheBlocksStart_AreNotDropped()
    {
        // The API starts every text block without citations, but one that did carry some must keep them.
        List<string> events =
        [
            StreamEvents.Start(),
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"","citations":[{"type":"char_location","cited_text":"abc","document_index":1,"document_title":"Doc 1","start_char_index":3,"end_char_index":6}]}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"pre"}}""",
            """{"type":"content_block_stop","index":0}""",
            .. StreamEvents.End(),
        ];

        List<ChatResponseUpdate> updates = await StreamAsync(events);

        TextContent cited = Assert.Single(ClaudeTestClient.Contents(updates).OfType<TextContent>(), content => content.Annotations is { Count: > 0 });
        AssertCitation(cited, 0, "abc", "Doc 1", 1, 3, 6);
    }

    [Fact]
    public async Task ACitationOfAnotherKind_IsPassedOnWithItsType_ForTheAppToRefuse()
    {
        List<string> events =
        [
            StreamEvents.Start(),
            .. StreamEvents.Text(0, "p.", new JsonObject
            {
                ["type"] = "page_location",
                ["cited_text"] = "pg",
                ["document_index"] = 0,
                ["document_title"] = "T",
                ["start_page_number"] = 1,
                ["end_page_number"] = 2,
            }),
            .. StreamEvents.End(),
        ];

        List<ChatResponseUpdate> updates = await StreamAsync(events);

        CitationAnnotation citation = Assert.IsType<CitationAnnotation>(Assert.Single(
            ClaudeTestClient.Contents(updates).OfType<TextContent>().SelectMany(content => content.Annotations ?? [])));
        Assert.Equal("page_location", citation.AdditionalProperties![ChatKeys.CitationType]);
        Assert.False(citation.AdditionalProperties.ContainsKey(ChatKeys.StartCharIndex));
    }

    [Fact]
    public async Task Usage_ArrivesAtTheStartWithTheInputSide_AndAtTheEndComplete()
    {
        List<ChatResponseUpdate> updates = await StreamAsync(CannedAnswer.Events);

        List<UsageContent> usages = [.. ClaudeTestClient.Contents(updates).OfType<UsageContent>()];
        Assert.Equal(2, usages.Count);

        // The input side, before any output: every input token, with the writes split by TTL.
        int first = updates.FindIndex(update => update.Contents.OfType<UsageContent>().Any());
        int firstOutput = updates.FindIndex(update => update.Contents.Any(content => content is TextContent or TextReasoningContent));
        Assert.True(first < firstOutput);
        AssertUsage(usages[0].Details, input: 12 + 2_080 + 20, read: 0, write1h: 2_080, write5m: 20, output: 0);

        // The final counts, once: the adapter's own usage, which lacks the split, is replaced.
        AssertUsage(usages[1].Details, input: 12 + 2_080 + 20, read: 0, write1h: 2_080, write5m: 20, output: 57);
        Assert.Same(usages[1], ClaudeTestClient.Contents(updates).Last(content => content is UsageContent));
    }

    [Fact]
    public async Task Usage_CountsCacheReadsAsCachedInput()
    {
        List<string> events = StreamEvents.Answer(StreamEvents.Text(0, "A."));
        events[0] = StreamEvents.Start(input: 10, write1h: 400, write5m: 100, read: 2_000);

        List<ChatResponseUpdate> updates = await StreamAsync(events);

        List<UsageContent> usages = [.. ClaudeTestClient.Contents(updates).OfType<UsageContent>()];
        AssertUsage(usages[0].Details, input: 2_510, read: 2_000, write1h: 400, write5m: 100, output: 0);
        AssertUsage(usages[^1].Details, input: 2_510, read: 2_000, write1h: 400, write5m: 100, output: 57);
    }

    [Theory]
    [InlineData(null, 3_000L, 0L)]
    [InlineData("ephemeral_5m_input_tokens", 3_000L, 0L)]
    [InlineData("ephemeral_1h_input_tokens", 2_000L, 1_000L)]
    public async Task AWriteTheBreakdownDoesNotAccountFor_IsCountedAtTheOneHourTtl(string? omitted, long write1h, long write5m)
    {
        List<string> events = StreamEvents.Answer(StreamEvents.Text(0, "A."));
        events[0] = omitted is null
            ? StreamEvents.Start(write1h: 2_000, write5m: 1_000, breakdown: false)
            : StreamEvents.Start(write1h: 2_000, write5m: 1_000, omitBreakdownField: omitted);

        List<ChatResponseUpdate> updates = await StreamAsync(events);

        Assert.All(
            ClaudeTestClient.Contents(updates).OfType<UsageContent>(),
            usage => AssertUsage(usage.Details, input: 12 + 3_000, read: 0, write1h: write1h, write5m: write5m, output: usage.Details.OutputTokenCount ?? 0));
    }

    [Fact]
    public async Task InputCountsRepeatedAtTheEnd_ReplaceThoseFromTheStart()
    {
        List<string> events =
        [
            StreamEvents.Start(input: 5, write1h: 300, write5m: 0),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":6,"output_tokens":3}}""",
            """{"type":"message_stop"}""",
        ];

        List<ChatResponseUpdate> updates = await StreamAsync(events);

        AssertUsage(ClaudeTestClient.Contents(updates).OfType<UsageContent>().Last().Details, input: 306, read: 0, write1h: 300, write5m: 0, output: 3);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("stop_sequence", "stop")]
    public async Task TheStopReason_TravelsWithTheFinishReason(string stopReason, string finishReason)
    {
        List<ChatResponseUpdate> updates = await StreamAsync([StreamEvents.Start(), .. StreamEvents.Text(0, "A."), .. StreamEvents.End(stopReason)]);

        ChatResponseUpdate finished = Assert.Single(updates, update => update.AdditionalProperties?.ContainsKey(ChatKeys.StopReason) == true);
        Assert.Equal(new ChatFinishReason(finishReason), finished.FinishReason);
        Assert.Equal(stopReason, finished.AdditionalProperties![ChatKeys.StopReason]);
        Assert.False(finished.AdditionalProperties.ContainsKey(ChatKeys.RefusalCategory));
    }

    [Fact]
    public async Task ARefusal_CarriesItsCategoryAndExplanation()
    {
        List<string> events =
        [
            StreamEvents.Start(),
            .. StreamEvents.End("refusal", stopDetails: new JsonObject { ["type"] = "refusal", ["category"] = "cyber", ["explanation"] = "Not this." }),
        ];

        List<ChatResponseUpdate> updates = await StreamAsync(events);

        ChatResponseUpdate finished = Assert.Single(updates, update => update.AdditionalProperties?.ContainsKey(ChatKeys.StopReason) == true);
        Assert.Equal(ChatFinishReason.ContentFilter, finished.FinishReason);
        Assert.Equal("refusal", finished.AdditionalProperties![ChatKeys.StopReason]);
        Assert.Equal("cyber", finished.AdditionalProperties[ChatKeys.RefusalCategory]);
        Assert.Equal("Not this.", finished.AdditionalProperties[ChatKeys.RefusalExplanation]);
    }

    [Fact]
    public async Task AStreamThatEndsWithoutMessageStop_HasEndedEarly()
    {
        List<ChatResponseUpdate> received = [];

        Exception? ex = await Record.ExceptionAsync(() => StreamAsync(CannedAnswer.Opening, received));

        // Whatever arrived, the response is incomplete.
        ChatProviderException failure = Assert.IsType<ChatProviderException>(ex);
        Assert.Equal(ChatProviderFailure.StreamEndedEarly, failure.Failure);
        Assert.Null(failure.StatusCode);
        Assert.Contains(received, update => update.Contents.OfType<UsageContent>().Any());
    }

    [Fact]
    public async Task AFailureAfterMessageStop_EndsTheStreamNormally_WithTheFinalUsage_AndIsLogged()
    {
        var log = new ListLoggerFactory();
        var stub = new StubHandler(_ => Sse.Response(new ThrowingOnDisposeStream(Sse.Bytes(CannedAnswer.Events))));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub, loggerFactory: log);

        List<ChatResponseUpdate> updates = await ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct);

        AssertUsage(ClaudeTestClient.Contents(updates).OfType<UsageContent>().Last().Details, input: 2_112, read: 0, write1h: 2_080, write5m: 20, output: 57);
        Assert.Equal(ChatFinishReason.Stop, updates.Last(update => update.FinishReason is not null).FinishReason);
        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(Chain(warning.Exception), exception => exception is IOException);
    }

    [Fact]
    public async Task AnErrorEventPartwayThroughTheStream_IsAStreamError_WithTheApisErrorType()
    {
        List<ChatResponseUpdate> received = [];
        List<string> events = [StreamEvents.Start(), .. StreamEvents.Thinking(0, "Checking."), StreamEvents.Error()];

        Exception? ex = await Record.ExceptionAsync(() => StreamAsync(events, received));

        ChatProviderException failure = Assert.IsType<ChatProviderException>(ex);
        Assert.Equal(ChatProviderFailure.StreamError, failure.Failure);
        Assert.Equal("overloaded_error", failure.ErrorType);
        Assert.Null(failure.StatusCode);
        Assert.NotNull(failure.InnerException);
        Assert.Contains(received, update => update.Contents.OfType<TextReasoningContent>().Any());
    }

    public static TheoryData<int, string, ChatProviderFailure, string?> StatusErrors => new()
    {
        { 401, ErrorBody("authentication_error", "invalid x-api-key"), ChatProviderFailure.Authentication, "authentication_error" },
        { 403, ErrorBody("permission_error", "Your API key does not have permission to use the specified resource."), ChatProviderFailure.Forbidden, "permission_error" },
        { 429, ErrorBody("rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit"), ChatProviderFailure.RateLimited, "rate_limit_error" },
        {
            400,
            ErrorBody("invalid_request_error", "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."),
            ChatProviderFailure.CreditExhausted,
            "invalid_request_error"
        },
        { 400, ErrorBody("billing_error", "This organization has no credit."), ChatProviderFailure.CreditExhausted, "billing_error" },
        { 400, ErrorBody("invalid_request_error", "messages: roles must alternate between \"user\" and \"assistant\""), ChatProviderFailure.ApiError, "invalid_request_error" },
        { 404, ErrorBody("not_found_error", "model: claude-nope"), ChatProviderFailure.ApiError, "not_found_error" },
        { 500, ErrorBody("api_error", "Internal server error"), ChatProviderFailure.ApiError, "api_error" },
        { 529, ErrorBody("overloaded_error", "Overloaded"), ChatProviderFailure.ApiError, "overloaded_error" },
    };

    [Theory]
    [MemberData(nameof(StatusErrors))]
    public async Task AnHttpError_IsTranslated_WithItsStatusAndErrorType(int status, string body, ChatProviderFailure kind, string? errorType)
    {
        var stub = new StubHandler(_ => WithoutRetryDelay(StubHandler.Json(body, (HttpStatusCode)status)));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct));

        ChatProviderException failure = Assert.IsType<ChatProviderException>(ex);
        Assert.Equal(kind, failure.Failure);
        Assert.Equal(status, failure.StatusCode);
        Assert.Equal(errorType, failure.ErrorType);
        Assert.NotNull(failure.InnerException);
        Assert.DoesNotContain("kkkk", failure.Message, StringComparison.Ordinal);

        // 429 and 5xx are retried before the stream starts (twice); nothing else is.
        Assert.Equal(RetryLoggingHandler.IsRetryableStatus(status) ? 1 + ClaudeChatClientProvider.MaxRetryCount : 1, stub.Requests.Count);
    }

    [Fact]
    public async Task AnUnrecognizedErrorType_IsLeftOut()
    {
        var stub = new StubHandler(_ => StubHandler.Json(ErrorBody("brand_new_error", "Something new."), HttpStatusCode.BadRequest));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        var failure = await Assert.ThrowsAsync<ChatProviderException>(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct));

        Assert.Equal(ChatProviderFailure.ApiError, failure.Failure);
        Assert.Equal(400, failure.StatusCode);
        Assert.Null(failure.ErrorType);
    }

    [Fact]
    public async Task AConnectionThatFails_IsAConnectionFailure_AfterTheRetries()
    {
        var stub = new StubHandler(_ => throw new HttpRequestException("Connection refused (simulated)."));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        var failure = await Assert.ThrowsAsync<ChatProviderException>(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct));

        Assert.Equal(ChatProviderFailure.Connection, failure.Failure);
        Assert.Null(failure.StatusCode);
        Assert.Equal(1 + ClaudeChatClientProvider.MaxRetryCount, stub.Requests.Count);
    }

    [Fact]
    public async Task ABodyThatBreaksAfterTheResponseStarted_IsAConnectionFailure_AndNotRetried()
    {
        var stub = new StubHandler(_ => Sse.Response(new BreakingStream(Sse.Bytes(CannedAnswer.Opening))));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        var failure = await Assert.ThrowsAsync<ChatProviderException>(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct));

        Assert.Equal(ChatProviderFailure.Connection, failure.Failure);
        Assert.Contains(Chain(failure), exception => exception is IOException);

        // A retry would pay the corpus read or write, and the output, a second time.
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Cancellation_IsNotTranslated()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(new StubHandler(_ => Sse.Response(body)));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High),
            ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]),
            stop.Token,
            update =>
            {
                if (update.Contents.OfType<TextReasoningContent>().Any())
                {
                    stop.Cancel();
                }
            }));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.NotEqual("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task MissingOptions_AreRefused()
    {
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(new StubHandler(_ => Sse.Response(CannedAnswer.Events)));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in provider.ForEffort(EffortLevel.High).GetStreamingResponseAsync([ClaudeTestClient.FirstQuestion()], null, Ct))
            {
            }
        });
    }

    [Fact]
    public async Task GetResponse_HoldsOneContentPerBlock_InBlockOrder_WithTheFinalUsageAndStopReason()
    {
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(new StubHandler(_ => Sse.Response(CannedAnswer.Events)));
        ChatRequest request = ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]);

        ChatResponse response = await provider.ForEffort(EffortLevel.High).GetResponseAsync(request.Messages, request.Options, Ct);

        ChatMessage message = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal("msg_canned", message.MessageId);
        Assert.Collection(
            message.Contents,
            content =>
            {
                var thinking = Assert.IsType<TextReasoningContent>(content);
                Assert.Equal(CannedAnswer.Thinking, thinking.Text);
                Assert.Equal(CannedAnswer.Signature, thinking.ProtectedData);
                Assert.Equal(0L, ClaudeTestClient.BlockIndex(content));
            },
            content =>
            {
                var redacted = Assert.IsType<TextReasoningContent>(content);
                Assert.True(IsRedacted(redacted));
                Assert.Equal("cmVkYWN0ZWQ=", redacted.ProtectedData);
            },
            content => Assert.Equal("Sumps must hold ", Assert.IsType<TextContent>(content).Text),
            content =>
            {
                var cited = Assert.IsType<TextContent>(content);
                Assert.Equal("a minimum of 5 gallons", cited.Text);
                Assert.Equal(3L, ClaudeTestClient.BlockIndex(content));
                Assert.Equal(
                    [CannedAnswer.CitedText1, CannedAnswer.CitedText2],
                    (cited.Annotations ?? []).OfType<CitationAnnotation>().Select(citation => citation.Snippet));
            },
            content => Assert.Equal("", Assert.IsType<TextContent>(content).Text),
            content => Assert.Equal(".", Assert.IsType<TextContent>(content).Text));

        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("end_turn", response.AdditionalProperties![ChatKeys.StopReason]);
        AssertUsage(response.Usage!, input: 2_112, read: 0, write1h: 2_080, write5m: 20, output: 57);
    }

    [Fact]
    public async Task GetResponse_FailsAsTheStreamDoes()
    {
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(new StubHandler(_ => Sse.Response(CannedAnswer.Opening)));
        ChatRequest request = ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]);

        var failure = await Assert.ThrowsAsync<ChatProviderException>(
            () => provider.ForEffort(EffortLevel.High).GetResponseAsync(request.Messages, request.Options, Ct));

        Assert.Equal(ChatProviderFailure.StreamEndedEarly, failure.Failure);
    }

    private static async Task<List<ChatResponseUpdate>> StreamAsync(IEnumerable<string> events, List<ChatResponseUpdate>? received = null)
    {
        var stub = new StubHandler(_ => Sse.Response(events));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);
        received ??= [];

        await ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct, received.Add);

        Assert.Single(stub.Requests);
        return received;
    }

    private static void AssertCitation(
        TextContent content, long blockIndex, string citedText, string? title, long documentIndex, long start, long end)
    {
        Assert.Equal("", content.Text);
        Assert.Equal(blockIndex, ClaudeTestClient.BlockIndex(content));
        CitationAnnotation citation = Assert.IsType<CitationAnnotation>(Assert.Single(content.Annotations!));
        Assert.Equal(citedText, citation.Snippet);
        Assert.Equal(title, citation.Title);
        AdditionalPropertiesDictionary properties = citation.AdditionalProperties!;
        Assert.Equal("char_location", properties[ChatKeys.CitationType]);
        Assert.True(properties.TryGetValue(ChatKeys.DocumentIndex, out long actualDocument));
        Assert.True(properties.TryGetValue(ChatKeys.StartCharIndex, out long actualStart));
        Assert.True(properties.TryGetValue(ChatKeys.EndCharIndex, out long actualEnd));
        Assert.Equal((documentIndex, start, end), (actualDocument, actualStart, actualEnd));
    }

    private static void AssertUsage(UsageDetails usage, long input, long read, long write1h, long write5m, long output)
    {
        Assert.Equal(input, usage.InputTokenCount);
        Assert.Equal(read, usage.CachedInputTokenCount);
        Assert.Equal(output, usage.OutputTokenCount);
        Assert.Equal(input + output, usage.TotalTokenCount);
        Assert.Equal(write1h, usage.AdditionalCounts?[ChatKeys.CacheWrite1hTokens]);
        Assert.Equal(write5m, usage.AdditionalCounts?[ChatKeys.CacheWrite5mTokens]);
    }

    private static bool IsRedacted(TextReasoningContent content) =>
        content.AdditionalProperties?.TryGetValue(ChatKeys.RedactedReasoning, out bool redacted) == true && redacted;

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static string ErrorBody(string type, string message) =>
        JsonSerializer.Serialize(new { type = "error", error = new { type, message } });

    /// <summary>The SDK retries 429 and 5xx twice; a zero retry-after keeps the test fast.</summary>
    private static HttpResponseMessage WithoutRetryDelay(HttpResponseMessage response)
    {
        response.Headers.Add("retry-after-ms", "0");
        return response;
    }
}
