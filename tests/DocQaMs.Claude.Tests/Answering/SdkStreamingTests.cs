using System.Diagnostics;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Claude.Tests.Answering;

/// <summary>
/// Plan item 1, checks 2 and 6, offline: how the SDK's stream behaves under the Claude pipeline, when
/// cancelled, when the body breaks, and when the body or the headers stall.
/// </summary>
public sealed class SdkStreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnePass_ForwardsEveryEvent_AndTheResponseKeepsCitationsAndSignatures()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        List<ChatResponseUpdate> updates = await ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct);

        Assert.Single(stub.Requests);

        // One update per event but ping, in order; any update without an event (the final usage) comes last.
        List<string> names = [.. updates.Select(ClaudeTestClient.Name)];
        Assert.Equal(CannedAnswer.ForwardedEventNames, names.Where(name => name != ClaudeTestClient.NoEvent));
        Assert.All(names.SkipWhile(name => name != ClaudeTestClient.NoEvent), name => Assert.Equal(ClaudeTestClient.NoEvent, name));

        ChatResponse response = updates.ToChatResponse();
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        TextReasoningContent thinking = Assert.IsType<TextReasoningContent>(
            response.Messages.SelectMany(message => message.Contents).First(content => content is TextReasoningContent));
        Assert.Equal(CannedAnswer.Signature, thinking.ProtectedData);
        Assert.Equal(
            [CannedAnswer.CitedText1, CannedAnswer.CitedText2],
            response.Messages.SelectMany(message => message.Contents).SelectMany(content => content.Annotations ?? [])
                .OfType<CitationAnnotation>().Select(citation => citation.Snippet));
    }

    [Fact]
    public async Task InputUsage_ArrivesBeforeTheRestOfTheBody_AndCancellingEndsTheStream()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        var stub = new StubHandler(_ => Sse.Response(body));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        long? write1hAtStart = null;

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High),
            ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]),
            stop.Token,
            update =>
            {
                if (update.Contents.OfType<UsageContent>().FirstOrDefault() is { } usage)
                {
                    write1hAtStart ??= usage.Details.AdditionalCounts?[ChatKeys.CacheWrite1hTokens];
                }

                // The Stop button: pressed while the body is still arriving.
                if (ClaudeTestClient.Name(update) == "content_block_delta:thinking_delta")
                {
                    stop.Cancel();
                }
            }));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(CannedAnswer.Write1h, write1hAtStart);
        Assert.NotEqual("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AStreamThatBreaksAfterTheResponseStarted_IsNotRetried()
    {
        var stub = new StubHandler(_ => Sse.Response(new BreakingStream(Sse.Bytes(CannedAnswer.Opening))));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct));

        // The transport's exception, translated: the answer service reports a connection error.
        Assert.Equal(ChatProviderFailure.Connection, Assert.IsType<ChatProviderException>(ex).Failure);

        // A retry would pay the corpus read or write, and the output, a second time.
        Assert.Single(stub.Requests);
    }

    /// <summary>
    /// A finding of plan item 1: contrary to the documentation of <c>ClientOptions.Timeout</c>, the SDK's
    /// timeout does not interrupt a streamed body that stalls after the headers. The answer service must
    /// therefore enforce each level's timeout itself, on the token it passes to the chat client. If this
    /// fails after an SDK upgrade, the SDK's behavior has changed; revisit that decision.
    /// </summary>
    [Fact]
    public async Task TheSdkTimeout_DoesNotInterruptAStalledBody()
    {
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(3));
        var stub = new StubHandler(_ => Sse.Response(body));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub, TimeSpan.FromMilliseconds(500));

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct));

        // The stall ran its full course, six times the timeout, and ended the body without message_stop.
        Assert.Equal("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // A body that ends without message_stop surfaces as an error, never as a partial answer passed off
        // as complete.
        Assert.Equal(ChatProviderFailure.StreamEndedEarly, Assert.IsType<ChatProviderException>(ex).Failure);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ACallerTokenThatTimesOut_InterruptsAStalledBody()
    {
        // What the answer service does instead of relying on the SDK's timeout.
        var body = new StallingStream(Sse.Bytes(CannedAnswer.Opening), TimeSpan.FromSeconds(30));
        var stub = new StubHandler(_ => Sse.Response(body));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), timeout.Token));

        watch.Stop();
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed} to end.");
        Assert.NotEqual("cap", await body.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task TheSdkTimeout_BoundsTheWaitForResponseHeaders()
    {
        var handler = new SlowHeadersHandler(TimeSpan.FromSeconds(60));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(handler, TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), Ct));

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}; the timeout did not bound the wait for headers.");

        // A timeout surfaces as a cancellation, like Stop, although the caller's token was never
        // cancelled, and the Claude middleware does not translate it: the answer service tells the two
        // apart itself.
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.False(Ct.IsCancellationRequested);

        // A change from SDK 12.47.0, which DocQa measured: 12.50.0 retries its own timeout like any other
        // failure before the response starts, so the whole wait can be (1 + MaxRetryCount) timeouts. The
        // answer service's own timeout still ends it after one. If this fails after an SDK upgrade, the
        // SDK's behavior has changed again; revisit the timeouts.
        Assert.Equal(1 + ClaudeChatClientProvider.MaxRetryCount, handler.Attempts);
    }

    [Fact]
    public async Task ACallerTokenThatTimesOut_EndsTheWaitForHeaders_WithoutARetry()
    {
        // What the answer service does: its own timeout, on the token, is not retried.
        var handler = new SlowHeadersHandler(TimeSpan.FromSeconds(60));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(handler);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));

        Exception? ex = await Record.ExceptionAsync(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High), ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]), timeout.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(1, handler.Attempts);
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
