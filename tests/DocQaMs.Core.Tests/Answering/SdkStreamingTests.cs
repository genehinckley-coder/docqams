using System.Diagnostics;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Plan item 1, checks 2 and 6, offline: how the SDK's stream behaves in the single-pass shape, when
/// cancelled, when the body breaks, and when the body stalls.
/// </summary>
public sealed class SdkStreamingTests
{
    [Fact]
    public async Task OnePass_ForwardsEveryEvent_AndTheAggregateKeepsCitationsAndSignatures()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using AnthropicClientProvider provider = Provider(stub, TimeSpan.FromMinutes(1));

        (Message message, List<string> names) = await SinglePass.StreamAsync(
            provider.ForEffort(EffortLevel.High), Request(), TestContext.Current.CancellationToken);

        Assert.Single(stub.Requests);
        Assert.Equal(CannedAnswer.ForwardedEventNames, names);

        Assert.Equal(6, message.Content.Count);
        Assert.True(message.Content[0].TryPickThinking(out ThinkingBlock? thinking));
        Assert.Equal(CannedAnswer.Thinking, thinking.Thinking);
        Assert.Equal(CannedAnswer.Signature, thinking.Signature);
        Assert.True(message.Content[1].TryPickRedactedThinking(out RedactedThinkingBlock? redacted));
        Assert.Equal("cmVkYWN0ZWQ=", redacted.Data);

        Assert.True(message.Content[3].TryPickText(out TextBlock? cited));
        Assert.Equal("a minimum of 5 gallons", cited.Text);
        Assert.Collection(
            cited.Citations ?? [],
            citation => AssertCharLocation(citation, CannedAnswer.CitedText1, 851, 880),
            citation => AssertCharLocation(citation, CannedAnswer.CitedText2, 950, 970));

        Assert.Equal(StopReason.EndTurn, message.StopReason?.Value());
        Assert.Equal(CannedAnswer.Write1h, (message.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0));
        Assert.Equal(CannedAnswer.Write5m, (message.Usage.CacheCreation?.Ephemeral5mInputTokens ?? 0));
        Assert.Equal(57, message.Usage.OutputTokens);
    }

    [Fact]
    public async Task InputUsage_ArrivesBeforeTheRestOfTheBody_AndCancellingEndsTheStream()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        var stub = new StubHandler(_ => Sse.Response(body));
        using AnthropicClientProvider provider = Provider(stub, TimeSpan.FromMinutes(1));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        long? write1hAtStart = null;

        Exception? ex = await Record.ExceptionAsync(() => SinglePass.StreamAsync(
            provider.ForEffort(EffortLevel.High),
            Request(),
            stop.Token,
            streamEvent =>
            {
                if (streamEvent.TryPickStart(out RawMessageStartEvent? start))
                {
                    write1hAtStart = (start.Message.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0);
                }

                // The Stop button: pressed while the body is still arriving.
                if (SinglePass.Name(streamEvent) == "content_block_delta:thinking_delta")
                {
                    stop.Cancel();
                }
            }));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(CannedAnswer.Write1h, write1hAtStart);
        Assert.NotEqual("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AStreamThatBreaksAfterTheResponseStarted_IsNotRetried()
    {
        var stub = new StubHandler(_ => Sse.Response(new BreakingStream(Sse.Bytes(CannedAnswer.Opening))));
        using AnthropicClientProvider provider = Provider(stub, TimeSpan.FromMinutes(1));

        Exception? ex = await Record.ExceptionAsync(() => SinglePass.StreamAsync(
            provider.ForEffort(EffortLevel.High), Request(), TestContext.Current.CancellationToken));

        // The transport's own exception, unwrapped: the answer service maps it to a connection error.
        Assert.IsType<IOException>(ex);

        // A retry would pay the corpus read or write, and the output, a second time.
        Assert.Single(stub.Requests);
    }

    /// <summary>
    /// A finding of plan item 1: contrary to the documentation of <c>ClientOptions.Timeout</c>, the SDK's
    /// timeout does not interrupt a streamed body that stalls after the headers. The answer service must
    /// therefore enforce each level's timeout itself, on the token it passes to <c>CreateStreaming</c>.
    /// If this fails after an SDK upgrade, the SDK's behavior has changed; revisit that decision.
    /// </summary>
    [Fact]
    public async Task TheSdkTimeout_DoesNotInterruptAStalledBody()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(3));
        var stub = new StubHandler(_ => Sse.Response(body));
        using AnthropicClientProvider provider = Provider(stub, TimeSpan.FromMilliseconds(500));

        Exception? ex = await Record.ExceptionAsync(() => SinglePass.StreamAsync(
            provider.ForEffort(EffortLevel.High), Request(), TestContext.Current.CancellationToken));

        // The stall ran its full course, six times the timeout, and ended the body without message_stop.
        Assert.Equal("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // A body that ends without message_stop surfaces as an error, never as a partial answer passed off
        // as complete.
        Assert.IsType<AnthropicInvalidDataException>(ex);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ACallerTokenThatTimesOut_InterruptsAStalledBody()
    {
        // What the answer service does instead of relying on the SDK's timeout.
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        var stub = new StubHandler(_ => Sse.Response(body));
        using AnthropicClientProvider provider = Provider(stub, TimeSpan.FromMinutes(1));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        Exception? ex = await Record.ExceptionAsync(() => SinglePass.StreamAsync(
            provider.ForEffort(EffortLevel.High), Request(), timeout.Token));

        watch.Stop();
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed} to end.");
        Assert.NotEqual("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task TheSdkTimeout_BoundsTheWaitForResponseHeaders()
    {
        var handler = new SlowHeadersHandler(TimeSpan.FromSeconds(60));
        using AnthropicClientProvider provider = Provider(handler, TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        Exception? ex = await Record.ExceptionAsync(() => SinglePass.StreamAsync(
            provider.ForEffort(EffortLevel.High), Request(), TestContext.Current.CancellationToken));

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}; the timeout did not bound the wait for headers.");

        // A timeout surfaces as a cancellation, like Stop, although the caller's token was never
        // cancelled: the answer service tells the two apart by its own token. It is not retried.
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, handler.Attempts);
    }

    private static void AssertCharLocation(TextCitation citation, string citedText, long start, long end)
    {
        Assert.True(citation.TryPickCitationCharLocation(out CitationCharLocation? location));
        Assert.Equal(citedText, location.CitedText);
        Assert.Equal(0, location.DocumentIndex);
        Assert.Equal(CannedAnswer.DocumentTitle, location.DocumentTitle);
        Assert.Equal(start, location.StartCharIndex);
        Assert.Equal(end, location.EndCharIndex);
    }

    private static AnthropicClientProvider Provider(DelegatingHandler innermost, TimeSpan timeout) =>
        AnthropicClientProvider.Create(SinglePass.FakeKey, SinglePass.Timeouts(timeout), [innermost]);

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

    private static MessageCreateParams Request() => MessageRequestBuilder.Create(
        ModelIds.ClaudeOpus5,
        EffortLevel.High,
        32_000,
        CacheAffecting.SystemPrompt,
        [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?")]);
}
