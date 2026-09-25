using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Plan item 4, offline and provider-neutral: one streamed turn, from request to <see cref="AnswerEvent.Completed"/>,
/// and every way it can end early, over a fake chat client that streams updates under the app's contract. What
/// the Claude provider sends and receives on the wire is tested in DocQaMs.Claude.Tests.
/// </summary>
public sealed class AnswerServiceTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAnswer_StreamsItsEventsInOrder_FromOneRequest()
    {
        using var harness = new AnswerHarness(CannedAnswer.Updates);
        List<AnswerEvent> events = [];

        await harness.CollectAsync(events, cancellationToken: TestToken);

        Assert.Single(harness.Client.Calls);
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

        // The final response keeps its metadata and the provider's stop reason.
        Assert.Equal(NeutralUpdates.ResponseId, result.Response.ResponseId);
        Assert.Equal(NeutralUpdates.ModelId, result.Response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, result.Response.FinishReason);
        Assert.Equal("end_turn", result.Response.AdditionalProperties?[ChatKeys.StopReason]);
        Assert.Equal(NeutralUpdates.ResponseId, result.Reply.MessageId);
        Assert.Equal(ChatRole.Assistant, result.Reply.Role);

        // The reply is one content per block, in block order, keeping what replay needs: the signature and the
        // citations, each on its own block.
        Assert.Equal([0L, 1L, 2L, 3L, 4L, 5L], result.Reply.Contents.Select(BlockIndexOf));
        TextReasoningContent thinking = Assert.IsType<TextReasoningContent>(result.Reply.Contents[0]);
        Assert.Equal(CannedAnswer.Thinking, thinking.Text);
        Assert.Equal(CannedAnswer.Signature, thinking.ProtectedData);
        TextReasoningContent redacted = Assert.IsType<TextReasoningContent>(result.Reply.Contents[1]);
        Assert.Equal(CannedAnswer.RedactedData, redacted.ProtectedData);
        Assert.True(redacted.AdditionalProperties?[ChatKeys.RedactedReasoning] is true);
        TextContent cited = Assert.IsType<TextContent>(result.Reply.Contents[3]);
        Assert.Equal("a minimum of 5 gallons", cited.Text);
        Assert.Equal(2, cited.Annotations?.Count);
        Assert.Null(Assert.IsType<TextContent>(result.Reply.Contents[2]).Annotations);
        Assert.Equal(5, ReplayConverter.ToReplay(result.Reply).Count);
    }

    [Theory]
    [InlineData(ModelIds.ClaudeOpus5, EffortLevel.Low, "low", 32_000)]
    [InlineData(ModelIds.ClaudeOpus5, EffortLevel.High, "high", 32_000)]
    [InlineData(ModelIds.ClaudeSonnet5, EffortLevel.XHigh, "xhigh", 64_000)]
    [InlineData(ModelIds.ClaudeSonnet5, EffortLevel.Max, "max", 128_000)]
    public async Task TheRequest_CarriesTheSettingsModelEffortAndOutputLimit_ToTheLevelsClient(
        string modelId, EffortLevel effort, string wire, int maxTokens)
    {
        using var harness = new AnswerHarness(CannedAnswer.Updates);

        await harness.CompleteAsync(AnswerSettings.Create(modelId, effort), cancellationToken: TestToken);

        // Each level has its own client, because each has its own timeout.
        Assert.Equal([effort], harness.Provider.Levels);
        ChatOptions options = Assert.Single(harness.Client.Calls).Options!;
        Assert.Equal(modelId, options.ModelId);
        Assert.Equal(maxTokens, options.MaxOutputTokens);
        Assert.Equal(CacheAffecting.SystemPrompt, options.Instructions);
        Assert.Equal(wire, options.AdditionalProperties?[ChatKeys.Effort]);
        Assert.Equal("5m", options.AdditionalProperties?[ChatKeys.ConversationCacheTtl]);
        Assert.Equal(ReasoningOutput.Summary, options.Reasoning?.Output);
    }

    [Fact]
    public async Task TheMessagesSent_AreTheConversation_WithTheCallersMessagesUnchanged()
    {
        using var harness = new AnswerHarness(CannedAnswer.Updates);
        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?");
        IReadOnlyList<ChatMessage> conversation =
        [
            first,
            MessageRequestBuilder.AssistantTurn([new TextContent("A1.")]),
            MessageRequestBuilder.FollowUpTurn("Q2?"),
        ];

        await harness.CompleteAsync(messages: conversation, cancellationToken: TestToken);

        IReadOnlyList<ChatMessage> sent = Assert.Single(harness.Client.Calls).Messages;
        Assert.Equal(3, sent.Count);
        Assert.Equal("5m", sent[0].Contents[^1].AdditionalProperties?[ChatKeys.CacheTtl]);
        Assert.Same(conversation[1], sent[1]);
        Assert.Same(conversation[2], sent[2]);
        Assert.Null(first.Contents[^1].AdditionalProperties);
    }

    [Fact]
    public async Task Costs_ArePricedAtTheSelectedModelsPrices()
    {
        var million = new InputTokens(1_000_000, 1_000_000, 1_000_000, 1_000_000);
        List<ChatResponseUpdate> updates =
        [
            NeutralUpdates.Start(million),
            .. NeutralUpdates.Text(0, "Answer."),
            .. NeutralUpdates.End(output: 1_000_000, tokens: million),
        ];
        using var harness = new AnswerHarness(updates);

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
    public async Task UsageStarted_IsTheFirstUsagesInputSide_AndTheResultTheLastUsage()
    {
        // A provider may report an output count on its first usage; it is a placeholder, not what was billed.
        UsageDetails first = NeutralUpdates.Usage(InputTokens.Default, output: 3);
        List<ChatResponseUpdate> updates =
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(first)]),
            .. NeutralUpdates.Text(0, "A."),
            .. NeutralUpdates.End(output: 99),
        ];
        using var harness = new AnswerHarness(updates);
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        Assert.Equal(new TokenUsage(12, 2_080, 20, 0, 0), Assert.IsType<AnswerEvent.UsageStarted>(received[0]).Usage);
        Assert.Single(received.OfType<AnswerEvent.UsageStarted>());
        Assert.Equal(new TokenUsage(12, 2_080, 20, 0, 99), Assert.IsType<AnswerEvent.Completed>(received[^1]).Result.Usage);
    }

    [Fact]
    public async Task AResponseWithNoUsage_CompletesAtNoCost_AndIsFlaggedNotCached()
    {
        List<ChatResponseUpdate> updates = [.. NeutralUpdates.Text(0, "A."), NeutralUpdates.End().First()];
        using var harness = new AnswerHarness(updates);
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        Assert.DoesNotContain(received, answerEvent => answerEvent is AnswerEvent.UsageStarted);
        AnswerResult result = Assert.IsType<AnswerEvent.Completed>(received[^1]).Result;
        Assert.Equal(new TokenUsage(0, 0, 0, 0, 0), result.Usage);
        Assert.Equal(TurnCost.Zero, result.Cost);
        Assert.Equal(AnswerResult.NotCachedWarning, result.CacheWarning);
    }

    [Fact]
    public async Task AResponseWithNoCacheActivity_IsFlaggedNotCached()
    {
        var none = new InputTokens(Write1h: 0, Write5m: 0, Read: 0);
        var readOnly = new InputTokens(Write1h: 0, Write5m: 0, Read: 5_000);

        using var cold = new AnswerHarness(Answer(none));
        using var warm = new AnswerHarness(Answer(readOnly));

        Assert.Equal(AnswerResult.NotCachedWarning, (await cold.CompleteAsync(cancellationToken: TestToken)).CacheWarning);
        Assert.Null((await warm.CompleteAsync(cancellationToken: TestToken)).CacheWarning);

        static List<ChatResponseUpdate> Answer(InputTokens tokens) =>
            [NeutralUpdates.Start(tokens), .. NeutralUpdates.Text(0, "A."), .. NeutralUpdates.End(tokens: tokens)];
    }

    [Fact]
    public async Task ASpanCitedTwice_GetsOneNumber_AndTheSameOffsetsInAnotherDocumentAnother()
    {
        List<ChatResponseUpdate> updates = NeutralUpdates.Answer(
            NeutralUpdates.Text(0, "First.", NeutralUpdates.Citation(0, 10)),
            NeutralUpdates.Text(1, " Second.", NeutralUpdates.Citation(0, 10), NeutralUpdates.Citation(20, 30), NeutralUpdates.Citation(20, 30)),
            NeutralUpdates.Text(2, " Elsewhere.", NeutralUpdates.Citation(0, 10, documentIndex: 1)));
        using var harness = new AnswerHarness(updates);
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
        // The API allows a citation without a title (plan item 1).
        List<ChatResponseUpdate> updates = NeutralUpdates.Answer(
            NeutralUpdates.Text(0, "Untitled.", NeutralUpdates.Citation(3, 9, "quoted", title: null)));
        using var harness = new AnswerHarness(updates);
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        var expected = new AnswerCitation(0, 3, 9, "quoted", null);
        Assert.Equal([expected], Assert.Single(received.OfType<AnswerEvent.TextBlockCompleted>()).Citations);
        Assert.Equal([expected], Assert.IsType<AnswerEvent.Completed>(received[^1]).Result.Sources);
    }

    [Fact]
    public async Task ContentWithoutABlockIndex_ContinuesABlockOfItsKind_OrStartsTheNext()
    {
        // The provider always tags its contents; without a tag the reader still keeps blocks apart.
        List<ChatResponseUpdate> updates =
        [
            NeutralUpdates.Start(),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Thinking.")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("One ")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("block.")]),
            .. NeutralUpdates.End(),
        ];
        using var harness = new AnswerHarness(updates);
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: TestToken);

        Assert.Equal(
            [new AnswerEvent.TextDelta(1, "One "), new AnswerEvent.TextDelta(1, "block.")],
            received.OfType<AnswerEvent.TextDelta>());
        AnswerResult result = Assert.IsType<AnswerEvent.Completed>(received[^1]).Result;
        Assert.Equal([0L, 1L], result.Reply.Contents.Select(BlockIndexOf));
        Assert.Equal("One block.", Assert.IsType<TextContent>(result.Reply.Contents[1]).Text);
    }

    [Fact]
    public async Task ACitationOfAnotherKind_FailsTheAnswer_RatherThanBeingKept()
    {
        List<ChatResponseUpdate> updates = NeutralUpdates.Answer(
            NeutralUpdates.Text(0, "Claim.", NeutralUpdates.RawCitation("page_location", "x")));
        using var harness = new AnswerHarness(updates);
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AnswerFailedException failure = AssertFailure(ex, AnswerFailureKind.Unexpected, harness);
        Assert.Equal("The response contained a citation or block this app does not handle. See the log.", failure.Message);
        Assert.IsType<NotSupportedException>(failure.InnerException);
    }

    [Fact]
    public async Task TwoKindsOfContentInOneBlock_FailTheAnswer()
    {
        List<ChatResponseUpdate> updates = NeutralUpdates.Answer(
            NeutralUpdates.Thinking(0, "Thinking."), NeutralUpdates.Text(0, "Text in the thinking block."));
        using var harness = new AnswerHarness(updates);

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: TestToken));

        AssertFailure(ex, AnswerFailureKind.Unexpected, harness);
    }

    [Fact]
    public async Task Stop_EndsTheAnswerAfterUsageStarted_ClosesTheStream_AndSendsNoCompleted()
    {
        FakeResponse response = new FakeResponse(CannedAnswer.Opening).ThenStall();
        using var harness = new AnswerHarness(new FakeChatClient(response));
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
        ChatCall call = Assert.Single(harness.Client.Calls);
        await call.Disposed.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.StartsWith("Answer stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_WhileTheStreamIsStalled_CancelsTheRead()
    {
        FakeResponse response = new FakeResponse(CannedAnswer.Opening).ThenStall();
        using var harness = new AnswerHarness(new FakeChatClient(response));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

        Task answer = harness.CollectAsync([], cancellationToken: stop.Token);
        await response.Stalled.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
        Assert.Equal("cancelled", await response.StallEnded.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
    }

    [Fact]
    public async Task ATransportErrorAfterStop_IsStillAStop()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        FakeResponse response = new FakeResponse(CannedAnswer.Opening)
            .ThenRun(stop.Cancel)
            .ThenThrow(new IOException("The connection was closed by Stop (simulated)."));
        using var harness = new AnswerHarness(new FakeChatClient(response));

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: stop.Token));

        OperationCanceledException stopped = Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsType<IOException>(stopped.InnerException);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task StopAfterTheResponseEnded_StillSendsCompleted()
    {
        // A finished answer has been paid for, so it is kept (plan item 6).
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        FakeResponse response = new FakeResponse(CannedAnswer.Updates).ThenRun(stop.Cancel);
        using var harness = new AnswerHarness(new FakeChatClient(response));
        List<AnswerEvent> received = [];

        await harness.CollectAsync(received, cancellationToken: stop.Token);

        Assert.True(stop.IsCancellationRequested);
        Assert.IsType<AnswerEvent.Completed>(received[^1]);
    }

    [Fact]
    public async Task StopBeforeTheRequest_SendsNothing()
    {
        using var harness = new AnswerHarness(CannedAnswer.Updates);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: stop.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Empty(harness.Client.Calls);
    }

    [Fact]
    public async Task AbandoningTheEnumeration_ClosesTheStream()
    {
        FakeResponse response = new FakeResponse(CannedAnswer.Opening).ThenStall();
        using var harness = new AnswerHarness(new FakeChatClient(response));

        await foreach (AnswerEvent answerEvent in harness.Service.StreamAsync(AnswerSettings.Default, AnswerHarness.FirstQuestion, TestToken))
        {
            Assert.IsType<AnswerEvent.UsageStarted>(answerEvent);
            break;
        }

        await Assert.Single(harness.Client.Calls).Disposed.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        Assert.False(response.Stalled.IsCompleted);
    }

    [Fact]
    public async Task ACompletedAnswer_ClosesItsStream()
    {
        using var harness = new AnswerHarness(CannedAnswer.Updates);

        await harness.CompleteAsync(cancellationToken: TestToken);

        Assert.True(Assert.Single(harness.Client.Calls).Disposed.IsCompleted);
    }

    [Fact]
    public async Task TheLevelsTimeout_EndsAStalledStream_AsATimeoutRatherThanAStop()
    {
        var time = new FakeTimeProvider(StoreHarness.Start);
        FakeResponse response = new FakeResponse(CannedAnswer.Opening).ThenStall();
        using var harness = new AnswerHarness(
            new FakeChatClient(response), options => options.RequestTimeouts[EffortLevel.High] = TimeSpan.FromMilliseconds(500), time);
        List<AnswerEvent> received = [];

        Task answer = harness.CollectAsync(received, cancellationToken: TestToken);
        await response.Stalled.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Exception? ex = await Record.ExceptionAsync(() => answer.WaitAsync(TimeSpan.FromSeconds(10), TestToken));

        AnswerFailedException failure = AssertFailure(ex, AnswerFailureKind.TimedOut, harness);
        Assert.Equal(
            "No complete answer within 0.5 seconds, the limit for High effort, so the request was ended. Ask again, or ask a narrower question.",
            failure.Message);
        Assert.False(TestToken.IsCancellationRequested);
        Assert.IsType<AnswerEvent.UsageStarted>(received[0]);
        Assert.Equal("cancelled", await response.StallEnded.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        Assert.Single(harness.Client.Calls);
    }

    [Fact]
    public async Task TheClientsOwnTimeout_IsATimeoutToo()
    {
        // The client's whole-request timeout equals the level's; it surfaces as a bare cancellation.
        var time = new FakeTimeProvider(StoreHarness.Start);
        FakeResponse response = new FakeResponse([])
            .ThenRun(() => time.Advance(TimeSpan.FromMinutes(19.5)))
            .ThenThrow(new TaskCanceledException("The request was canceled due to the configured timeout (simulated)."));
        using var harness = new AnswerHarness(new FakeChatClient(response), time: time);

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: TestToken));

        AnswerFailedException failure = AssertFailure(ex, AnswerFailureKind.TimedOut, harness);
        Assert.Equal(
            "No complete answer within 20 minutes, the limit for High effort, so the request was ended. Ask again, or ask a narrower question.",
            failure.Message);
    }

    [Fact]
    public async Task ACancellationNeitherStopNorATimeoutAskedFor_IsNotCalledATimeout()
    {
        var time = new FakeTimeProvider(StoreHarness.Start);
        FakeResponse response = new FakeResponse([])
            .ThenRun(() => time.Advance(TimeSpan.FromMinutes(18)))
            .ThenThrow(new TaskCanceledException("Cancelled by a handler (simulated)."));
        using var harness = new AnswerHarness(new FakeChatClient(response), time: time);

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync([], cancellationToken: TestToken));

        AnswerFailedException failure = AssertFailure(ex, AnswerFailureKind.Unexpected, harness);
        Assert.Equal(
            "The request was cancelled, though neither Stop nor a timeout asked for it. Ask again; if it persists, see the log.",
            failure.Message);
    }

    [Fact]
    public async Task AFailureToCloseTheStream_DoesNotReplaceACompletedAnswer()
    {
        FakeResponse response = new FakeResponse(CannedAnswer.Updates).ThrowingOnDispose(new IOException("Closing the body failed (simulated)."));
        using var harness = new AnswerHarness(new FakeChatClient(response));

        AnswerResult result = await harness.CompleteAsync(cancellationToken: TestToken);

        Assert.Null(result.StopWarning);
        Assert.Equal(2, result.Sources.Count);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is IOException);
    }

    public static TheoryData<ChatProviderFailure, AnswerFailureKind> ProviderFailures => new()
    {
        { ChatProviderFailure.Authentication, AnswerFailureKind.Authentication },
        { ChatProviderFailure.Forbidden, AnswerFailureKind.Authentication },
        { ChatProviderFailure.RateLimited, AnswerFailureKind.RateLimited },
        { ChatProviderFailure.CreditExhausted, AnswerFailureKind.CreditExhausted },
        { ChatProviderFailure.ApiError, AnswerFailureKind.ApiError },
        { ChatProviderFailure.StreamError, AnswerFailureKind.ApiError },
        { ChatProviderFailure.StreamEndedEarly, AnswerFailureKind.StreamEndedEarly },
        { ChatProviderFailure.Connection, AnswerFailureKind.Connection },
    };

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task AProviderFailureBeforeTheStream_IsMappedToAShortMessage_AndLoggedInFull(ChatProviderFailure failure, AnswerFailureKind kind)
    {
        FakeResponse response = new FakeResponse([]).ThenThrow(Failure(failure));
        using var harness = new AnswerHarness(new FakeChatClient(response));
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AssertFailure(ex, kind, harness);
        Assert.Empty(received);
    }

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task AProviderFailurePartwayThroughTheStream_KeepsTheEventsBeforeIt(ChatProviderFailure failure, AnswerFailureKind kind)
    {
        FakeResponse response = new FakeResponse([NeutralUpdates.Start(), .. NeutralUpdates.Thinking(0, "Checking.")])
            .ThenThrow(Failure(failure));
        using var harness = new AnswerHarness(new FakeChatClient(response));
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AssertFailure(ex, kind, harness);
        Assert.IsType<AnswerEvent.UsageStarted>(received[0]);
        Assert.DoesNotContain(received, answerEvent => answerEvent is AnswerEvent.Completed);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(HttpRequestException))]
    public async Task AnUntranslatedTransportFailure_IsAConnectionError(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "The connection was reset (simulated).")!;
        FakeResponse response = new FakeResponse(CannedAnswer.Opening).ThenThrow(exception);
        using var harness = new AnswerHarness(new FakeChatClient(response));
        List<AnswerEvent> received = [];

        Exception? ex = await Record.ExceptionAsync(() => harness.CollectAsync(received, cancellationToken: TestToken));

        AssertFailure(ex, AnswerFailureKind.Connection, harness);
        Assert.IsType<AnswerEvent.UsageStarted>(received[0]);
        Assert.Single(harness.Client.Calls);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException), "API key file not found: D:\\keys\\missing.txt.")]
    [InlineData(typeof(InvalidDataException), "API key file is empty: D:\\keys\\empty.txt")]
    public async Task AKeyFileTheProviderCannotLoad_IsACredentialsFailure_AndNothingIsSent(Type exceptionType, string message)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, message)!;
        var logger = new ListLogger<AnswerService>();
        var service = new AnswerService(
            () => throw exception,
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
        Assert.Equal($"Cannot load the API key: {message}", failure.Message);
        Assert.Same(exception, failure.InnerException);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception == failure.InnerException);
    }

    [Fact]
    public async Task AnInvalidConversation_IsRefusedBeforeAnythingIsSent()
    {
        using var harness = new AnswerHarness(CannedAnswer.Updates);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CollectAsync([], messages: [], cancellationToken: TestToken));

        Assert.Empty(harness.Client.Calls);
        Assert.Empty(harness.Provider.Levels);
    }

    private static ChatProviderException Failure(ChatProviderFailure failure) => new(
        failure,
        $"{failure} (simulated).",
        new InvalidOperationException("The provider SDK's exception (simulated)."),
        statusCode: failure == ChatProviderFailure.ApiError ? 500 : null,
        errorType: failure is ChatProviderFailure.ApiError or ChatProviderFailure.StreamError ? "overloaded_error" : null);

    private static long? BlockIndexOf(AIContent content) =>
        content.AdditionalProperties is { } properties && properties.TryGetValue(ChatKeys.BlockIndex, out long index) ? index : null;

    private static void AssertBlock(AnswerEvent answerEvent, int index, int[] numbers)
    {
        var block = Assert.IsType<AnswerEvent.TextBlockCompleted>(answerEvent);
        Assert.Equal(index, block.BlockIndex);
        Assert.Equal(numbers, block.SourceNumbers);
    }

    private static AnswerFailedException AssertFailure(Exception? ex, AnswerFailureKind kind, AnswerHarness harness)
    {
        AnswerFailedException failure = Assert.IsType<AnswerFailedException>(ex);
        Assert.Equal(kind, failure.Kind);
        Assert.NotNull(failure.InnerException);

        var entry = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Same(failure.InnerException, entry.Exception);
        return failure;
    }
}
