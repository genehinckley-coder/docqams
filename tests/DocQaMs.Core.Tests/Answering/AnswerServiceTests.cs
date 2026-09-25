using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Plan item 4, offline: one streamed turn, from request to <see cref="AnswerEvent.Completed"/>, and every
/// way it can end early.
/// </summary>
public sealed class AnswerServiceTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAnswer_StreamsItsEventsInOrder_FromOneRequest()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using var harness = new AnswerHarness(stub);
        List<AnswerEvent> events = [];

        await harness.CollectAsync(events, cancellationToken: TestToken);

        Assert.Single(stub.Requests);
        Assert.Collection(
            events,
            e => Assert.Equal(new TokenUsage(12, CannedAnswer.Write1h, CannedAnswer.Write5m, 0, 0), Assert.IsType<AnswerEvent.UsageStarted>(e).Usage),
            e => Assert.Equal(new AnswerEvent.ThinkingDelta("The user asks about sumps. "), e),
            e => Assert.Equal(new AnswerEvent.ThinkingDelta("Section 175.410 applies."), e),
            e => Assert.IsType<AnswerEvent.ThinkingRedacted>(e),
            e => Assert.Equal(new AnswerEvent.TextDelta(2, "Sumps must hold "), e),
            e => AssertBlock(e, 2, []),
            e => Assert.Equal(new AnswerEvent.TextDelta(3, "a minimum of 5 gallons"), e),
            e => AssertBlock(e, 3, [1, 2]),
            e => AssertBlock(e, 4, []),
            e => Assert.Equal(new AnswerEvent.TextDelta(5, "."), e),
            e => AssertBlock(e, 5, []),
            e => Assert.IsType<AnswerEvent.Completed>(e));

        AnswerResult result = Assert.IsType<AnswerEvent.Completed>(events[^1]).Result;
        Assert.Equal(AnswerSettings.Default, result.Settings);
        Assert.Equal(new TokenUsage(12, CannedAnswer.Write1h, CannedAnswer.Write5m, 0, 57), result.Usage);
        Assert.Null(result.StopWarning);
        Assert.Null(result.CacheWarning);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Collection(
            result.Sources,
            source => Assert.Equal(new AnswerCitation(0, 851, 880, CannedAnswer.CitedText1, CannedAnswer.DocumentTitle), source),
            source => Assert.Equal(new AnswerCitation(0, 950, 970, CannedAnswer.CitedText2, CannedAnswer.DocumentTitle), source));

        // The final message keeps what replay needs: the signature and the citations.
        Assert.True(result.Message.Content[0].TryPickThinking(out ThinkingBlock? thinking));
        Assert.Equal(CannedAnswer.Signature, thinking.Signature);
        Assert.Equal(5, ReplayConverter.ToParams(result.Message.Content).Count);
    }

    [Theory]
    [InlineData(ModelIds.ClaudeOpus5, EffortLevel.Low, "low", 32_000)]
    [InlineData(ModelIds.ClaudeOpus5, EffortLevel.High, "high", 32_000)]
    [InlineData(ModelIds.ClaudeSonnet5, EffortLevel.XHigh, "xhigh", 64_000)]
    [InlineData(ModelIds.ClaudeSonnet5, EffortLevel.Max, "max", 128_000)]
    public async Task TheRequest_CarriesTheSettingsModelEffortAndOutputLimit(string modelId, EffortLevel effort, string wire, int maxTokens)
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using var harness = new AnswerHarness(stub);

        await harness.CompleteAsync(AnswerSettings.Create(modelId, effort), TestToken);

        using JsonDocument body = JsonDocument.Parse(Assert.Single(stub.Requests).Body!);
        JsonElement root = body.RootElement;
        Assert.Equal(modelId, root.GetProperty("model").GetString());
        Assert.Equal(wire, root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(maxTokens, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(CacheAffecting.SystemPrompt, root.GetProperty("system").GetString());
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("summarized", root.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("5m", root.GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Costs_ArePricedAtTheSelectedModelsPrices()
    {
        List<string> events =
        [
            StreamEvents.Start(input: 1_000_000, write1h: 1_000_000, write5m: 1_000_000, read: 1_000_000),
            .. StreamEvents.Text(0, "Answer."),
            .. StreamEvents.End(output: 1_000_000),
        ];
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(events)));

        List<AnswerEvent> opus = [];
        await harness.CollectAsync(opus, AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.High), cancellationToken: TestToken);
        List<AnswerEvent> sonnet = [];
        await harness.CollectAsync(sonnet, AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.High), cancellationToken: TestToken);

        // The input side is priced as soon as it arrives, with no output yet.
        Assert.Equal(new TurnCost(5m, 10m, 6.25m, 0.5m, 0m), Assert.IsType<AnswerEvent.UsageStarted>(opus[0]).Cost);
        Assert.Equal(new TurnCost(2m, 4m, 2.5m, 0.2m, 0m), Assert.IsType<AnswerEvent.UsageStarted>(sonnet[0]).Cost);

        Assert.Equal(46.75m, Assert.IsType<AnswerEvent.Completed>(opus[^1]).Result.Cost.Total);
        Assert.Equal(18.7m, Assert.IsType<AnswerEvent.Completed>(sonnet[^1]).Result.Cost.Total);
    }

    [Fact]
    public async Task AWriteWithoutATtlBreakdown_IsCountedAtTheOneHourTtl()
    {
        List<string> events = [StreamEvents.Start(write1h: 2_000, write5m: 1_000, breakdown: false), .. StreamEvents.Text(0, "A."), .. StreamEvents.End()];
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(events)));
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        TokenUsage usage = Assert.IsType<AnswerEvent.UsageStarted>(received[0]).Usage;
        Assert.Equal(3_000, usage.CacheWrite1h);
        Assert.Equal(0, usage.CacheWrite5m);
        Assert.Equal(3_000, Assert.IsType<AnswerEvent.Completed>(received[^1]).Result.Usage.CacheWrite1h);
    }

    [Theory]
    [InlineData("ephemeral_5m_input_tokens", 3_000, 0)]
    [InlineData("ephemeral_1h_input_tokens", 2_000, 1_000)]
    public async Task APartialTtlBreakdown_CountsWhatItDoesNotAccountForAtTheOneHourTtl(string omitted, long expected1h, long expected5m)
    {
        List<string> events =
        [
            StreamEvents.Start(write1h: 2_000, write5m: 1_000, omitBreakdownField: omitted),
            .. StreamEvents.Text(0, "A."),
            .. StreamEvents.End(),
        ];
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(events)));
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        TokenUsage started = Assert.IsType<AnswerEvent.UsageStarted>(received[0]).Usage;
        TokenUsage final = Assert.IsType<AnswerEvent.Completed>(received[^1]).Result.Usage;
        Assert.All([started, final], usage =>
        {
            Assert.Equal(expected1h, usage.CacheWrite1h);
            Assert.Equal(expected5m, usage.CacheWrite5m);
        });
    }

    [Fact]
    public async Task AResponseWithNoCacheActivity_IsFlaggedNotCached()
    {
        List<string> none = [StreamEvents.Start(write1h: 0, write5m: 0, read: 0), .. StreamEvents.Text(0, "A."), .. StreamEvents.End()];
        List<string> readOnly = [StreamEvents.Start(write1h: 0, write5m: 0, read: 5_000), .. StreamEvents.Text(0, "A."), .. StreamEvents.End()];

        using var cold = new AnswerHarness(new StubHandler(_ => Sse.Response(none)));
        using var warm = new AnswerHarness(new StubHandler(_ => Sse.Response(readOnly)));

        Assert.Equal(AnswerResult.NotCachedWarning, (await cold.CompleteAsync(cancellationToken: TestToken)).CacheWarning);
        Assert.Null((await warm.CompleteAsync(cancellationToken: TestToken)).CacheWarning);
    }

    [Fact]
    public async Task ASpanCitedTwice_GetsOneNumber_AndTheSameOffsetsInAnotherDocumentAnother()
    {
        List<string> events = StreamEvents.Answer(
            StreamEvents.Text(0, "First.", StreamEvents.Citation(0, 10)),
            StreamEvents.Text(1, " Second.", StreamEvents.Citation(0, 10), StreamEvents.Citation(20, 30), StreamEvents.Citation(20, 30)),
            StreamEvents.Text(2, " Elsewhere.", StreamEvents.Citation(0, 10, documentIndex: 1)));
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(events)));
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        List<AnswerEvent.TextBlockCompleted> blocks = [.. received.OfType<AnswerEvent.TextBlockCompleted>()];
        Assert.Equal([[1], [1, 2], [3]], blocks.Select(block => block.SourceNumbers.ToArray()).ToArray());
        Assert.Equal(3, blocks[1].Citations.Count);
        AnswerResult result = Assert.IsType<AnswerEvent.Completed>(received[^1]).Result;
        Assert.Equal([(0L, 0L, 10L), (0L, 20L, 30L), (1L, 0L, 10L)], result.Sources.Select(source => source.SpanKey).ToArray());
    }

    [Fact]
    public async Task ACitationWithNoDocumentTitle_IsStillRead()
    {
        // The SDK does not recognize such a citation (plan item 1), which the API allows.
        List<string> events = StreamEvents.Answer(StreamEvents.Text(0, "Untitled.", StreamEvents.Citation(3, 9, "quoted", title: null)));
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(events)));
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        var expected = new AnswerCitation(0, 3, 9, "quoted", null);
        Assert.Equal([expected], Assert.Single(received.OfType<AnswerEvent.TextBlockCompleted>()).Citations);
        Assert.Equal([expected], Assert.IsType<AnswerEvent.Completed>(received[^1]).Result.Sources);
    }

    [Fact]
    public async Task Stop_EndsTheAnswerAfterUsageStarted_ClosesTheStream_AndSendsNoCompleted()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        var stub = new StubHandler(_ => Sse.Response(body));
        using var harness = new AnswerHarness(stub);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(
            received,
            onEvent: answerEvent =>
            {
                // The Stop button, pressed while the answer is still arriving.
                if (answerEvent is AnswerEvent.ThinkingDelta)
                {
                    stop.Cancel();
                }
            },
            cancellationToken: stop.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsType<AnswerEvent.UsageStarted>(received[0]);
        Assert.DoesNotContain(received, answerEvent => answerEvent is AnswerEvent.Completed);
        Assert.NotEqual("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        Assert.Single(stub.Requests);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task StopBeforeTheRequest_SendsNothing()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using var harness = new AnswerHarness(stub);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: stop.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AbandoningTheEnumeration_ClosesTheStream()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(body)));

        await foreach (AnswerEvent answerEvent in harness.Service.StreamAsync(AnswerSettings.Default, AnswerHarness.FirstQuestion, TestToken))
        {
            Assert.IsType<AnswerEvent.UsageStarted>(answerEvent);
            break;
        }

        // No read is pending after the break, so the body is checked for disposal, not for a cancelled read.
        await body.Disposed.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
    }

    [Fact]
    public async Task TheLevelsTimeout_EndsAStalledBody_AsATimeoutRatherThanAStop()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        var stub = new StubHandler(_ => Sse.Response(body));
        using var harness = new AnswerHarness(stub, options => options.RequestTimeouts[EffortLevel.High] = TimeSpan.FromMilliseconds(500));
        List<AnswerEvent> received = [];
        var watch = Stopwatch.StartNew();

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        watch.Stop();
        AnswerFailedException failure = Assert.IsType<AnswerFailedException>(ex);
        Assert.Equal(AnswerFailureKind.TimedOut, failure.Kind);
        Assert.Contains("0.5 seconds, the limit for High effort", failure.Message, StringComparison.Ordinal);
        Assert.False(TestToken.IsCancellationRequested);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed} to end.");
        Assert.IsType<AnswerEvent.UsageStarted>(received[0]);
        Assert.NotEqual("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        Assert.Single(stub.Requests);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception == failure.InnerException);
    }

    [Fact]
    public async Task TheSdksTimeoutOnTheResponseHeaders_IsATimeoutToo()
    {
        var handler = new SlowHeadersHandler(TimeSpan.FromSeconds(60));
        using var harness = new AnswerHarness(handler, sdkTimeout: TimeSpan.FromMilliseconds(500));

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: TestToken));

        Assert.Equal(AnswerFailureKind.TimedOut, Assert.IsType<AnswerFailedException>(ex).Kind);
        Assert.False(TestToken.IsCancellationRequested);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ACancellationNeitherStopNorATimeoutAskedFor_IsNotCalledATimeout()
    {
        var stub = new StubHandler(_ => throw new TaskCanceledException("Cancelled by a handler (simulated)."));
        using var harness = new AnswerHarness(stub);

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: TestToken));

        AssertFailure(ex, AnswerFailureKind.Unexpected, harness);
    }

    [Fact]
    public async Task AFailureToCloseTheStream_DoesNotReplaceACompletedAnswer()
    {
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(new ThrowingOnDisposeStream(Sse.Bytes(CannedAnswer.Events)))));

        AnswerResult result = await harness.CompleteAsync(cancellationToken: TestToken);

        Assert.Null(result.StopWarning);
        Assert.Equal(2, result.Sources.Count);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);

        // The SDK closes the response inside its final read, after message_stop (found by this test).
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is IOException);
    }

    public static TheoryData<int, string, AnswerFailureKind> StatusErrors => new()
    {
        { 401, ErrorBody("authentication_error", "invalid x-api-key"), AnswerFailureKind.Authentication },
        { 403, ErrorBody("permission_error", "Your API key does not have permission to use the specified resource."), AnswerFailureKind.Authentication },
        { 429, ErrorBody("rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit"), AnswerFailureKind.RateLimited },
        {
            400,
            ErrorBody("invalid_request_error", "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."),
            AnswerFailureKind.CreditExhausted
        },
        { 400, ErrorBody("billing_error", "This organization has no credit."), AnswerFailureKind.CreditExhausted },
        { 400, ErrorBody("invalid_request_error", "messages: roles must alternate between \"user\" and \"assistant\""), AnswerFailureKind.ApiError },
        { 500, ErrorBody("api_error", "Internal server error"), AnswerFailureKind.ApiError },
    };

    [Theory]
    [MemberData(nameof(StatusErrors))]
    public async Task AnApiError_IsMappedToAShortMessage_AndLoggedInFull(int status, string body, AnswerFailureKind kind)
    {
        var stub = new StubHandler(_ => WithoutRetryDelay(StubHandler.Json(body, (HttpStatusCode)status)));
        using var harness = new AnswerHarness(stub);
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AssertFailure(ex, kind, harness);
        Assert.Empty(received);
    }

    [Fact]
    public async Task AnErrorEventPartwayThroughTheStream_IsAnApiError()
    {
        List<string> events = [StreamEvents.Start(), .. StreamEvents.Thinking(0, "Checking."), StreamEvents.Error()];
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(events)));
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AssertFailure(ex, AnswerFailureKind.ApiError, harness);
        Assert.IsType<AnswerEvent.UsageStarted>(received[0]);
    }

    [Fact]
    public async Task AStreamThatBreaksAfterTheResponseStarted_IsAConnectionError_AndNotRetried()
    {
        var stub = new StubHandler(_ => Sse.Response(new BreakingStream(Sse.Bytes(CannedAnswer.Opening))));
        using var harness = new AnswerHarness(stub);
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AssertFailure(ex, AnswerFailureKind.Connection, harness);
        Assert.IsType<AnswerEvent.UsageStarted>(received[0]);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AStreamThatEndsWithoutMessageStop_IsReportedAsEndedEarly()
    {
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(CannedAnswer.Opening)));
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AssertFailure(ex, AnswerFailureKind.StreamEndedEarly, harness);
        Assert.DoesNotContain(received, answerEvent => answerEvent is AnswerEvent.Completed);
    }

    [Fact]
    public async Task AMissingKeyFile_IsACredentialsFailure_AndNothingIsSent()
    {
        var logger = new ListLogger<AnswerService>();
        var service = new AnswerService(
            () => throw new FileNotFoundException("API key file not found: D:\\keys\\missing.txt."),
            Microsoft.Extensions.Options.Options.Create(TestSettings.ValidOptions()),
            TimeProvider.System,
            logger);

        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (AnswerEvent _ in service.StreamAsync(AnswerSettings.Default, AnswerHarness.FirstQuestion, TestToken))
            {
            }
        });

        AnswerFailedException failure = Assert.IsType<AnswerFailedException>(ex);
        Assert.Equal(AnswerFailureKind.Credentials, failure.Kind);
        Assert.StartsWith("Cannot load the API key: API key file not found", failure.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception == failure.InnerException);
    }

    [Fact]
    public async Task AnInvalidConversation_IsRefusedBeforeAnythingIsSent()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using var harness = new AnswerHarness(stub);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CollectAsync([], messages: [], cancellationToken: TestToken));

        Assert.Empty(stub.Requests);
    }

    private static void AssertBlock(AnswerEvent answerEvent, int index, int[] numbers)
    {
        var block = Assert.IsType<AnswerEvent.TextBlockCompleted>(answerEvent);
        Assert.Equal(index, block.BlockIndex);
        Assert.Equal(numbers, block.SourceNumbers);
    }

    private static void AssertFailure(Exception? ex, AnswerFailureKind kind, AnswerHarness harness)
    {
        AnswerFailedException failure = Assert.IsType<AnswerFailedException>(ex);
        Assert.Equal(kind, failure.Kind);
        Assert.NotNull(failure.InnerException);
        Assert.DoesNotContain("kkkk", failure.Message, StringComparison.Ordinal);

        var entry = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Same(failure.InnerException, entry.Exception);
        Assert.DoesNotContain("kkkk", entry.Message, StringComparison.Ordinal);
    }

    private static string ErrorBody(string type, string message) =>
        JsonSerializer.Serialize(new { type = "error", error = new { type, message } });

    /// <summary>The SDK retries 429 and 5xx twice; a zero retry-after keeps the test fast.</summary>
    private static HttpResponseMessage WithoutRetryDelay(HttpResponseMessage response)
    {
        response.Headers.Add("retry-after-ms", "0");
        return response;
    }

    /// <summary>Holds every request for <paramref name="delay"/> before any header is sent.</summary>
    private sealed class SlowHeadersHandler(TimeSpan delay) : DelegatingHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            await Task.Delay(delay, cancellationToken);
            return Sse.Response(CannedAnswer.Events);
        }
    }
}
