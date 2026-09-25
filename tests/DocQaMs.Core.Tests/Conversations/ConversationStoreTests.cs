using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Core.Tests.Conversations;

/// <summary>
/// Plan items 6 and 11: conversation state, the commit rules the prototype learned the hard way, the settings
/// lock, Stop, and the cache checks, offline against a scripted answer service.
/// </summary>
public sealed class ConversationStoreTests : IDisposable
{
    private readonly StoreHarness _harness = new();

    private ConversationStore Store => _harness.Store;

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// Every failure kind that can reach the store from a request already sent.
    /// <see cref="AnswerFailureKind.Credentials"/> has its own test, because nothing is sent and the turn
    /// is known to have cost nothing; <see cref="AnswerFailureKind.Unexpected"/> is raised by the store
    /// itself, not by the answer service.
    /// </summary>
    public static TheoryData<AnswerFailureKind> FailureKinds =>
    [
        AnswerFailureKind.Authentication,
        AnswerFailureKind.RateLimited,
        AnswerFailureKind.CreditExhausted,
        AnswerFailureKind.ApiError,
        AnswerFailureKind.Connection,
        AnswerFailureKind.StreamEndedEarly,
        AnswerFailureKind.TimedOut,
    ];

    // --- Asking and committing ---------------------------------------------------------------------

    [Fact]
    public async Task AFirstQuestion_IsBuiltFromTheCorpus_AndCommittedOnlyAfterItCompletes()
    {
        Guid id = Store.Create().Id;
        AnswerResult result = await Replies.TextAsync();

        Task turn = _harness.AskAsync(id, "  What must a sump hold?  ");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();

        ChatMessage first = Assert.Single(answer.Messages);
        Assert.Equal(ChatRole.User, first.Role);
        Assert.Equal(_harness.Corpus.Documents.Count + 1, first.Contents.Count);
        Assert.All(first.Contents.SkipLast(1), content => Assert.IsType<CitableDocumentContent>(content));
        Assert.Equal("What must a sump hold?", Assert.IsType<TextContent>(first.Contents[^1]).Text);
        Assert.Equal(AnswerSettings.Default, answer.Settings);

        ConversationSnapshot inFlight = Store.Get(id)!;
        Assert.Empty(inFlight.Messages);
        Assert.True(inFlight.IsAnswering);
        Assert.True(inFlight.SettingsLocked);
        Assert.Equal("What must a sump hold?", inFlight.Title);

        answer.Answer(result);
        await StoreHarness.WaitAsync(turn);

        ConversationSnapshot done = Store.Get(id)!;
        Assert.Collection(
            done.Messages,
            message => Assert.Same(first, message),
            message => Assert.Equal(ChatRole.Assistant, message.Role));
        AnswerRecord record = Assert.Single(done.Answers);
        Assert.Equal(TurnOutcome.Completed, record.Outcome);
        Assert.True(record.IsCommitted);
        Assert.Same(result, record.Result);
        Assert.NotNull(record.Quotes);
        Assert.Equal(new CacheReport(CacheStatus.ColdWrite), record.Cache);
        Assert.Empty(record.Warnings);
        Assert.Null(record.Notice);
        Assert.Null(record.CostNote);
        Assert.Equal(result.Cost, record.Cost);
        Assert.Equal("An answer.", Assert.Single(record.Segments).Text);
        Assert.Same(_harness.Corpus, record.Corpus);

        Assert.Null(done.InProgress);
        Assert.Null(done.ReturnedQuestion);
        Assert.Equal(TurnOutcome.Completed, done.LastOutcome);
        Assert.Same(_harness.Corpus, done.Corpus);
        Assert.Equal(new ConversationCost(result.Cost, 0, 0), done.Cost);
        Assert.Equal(result.Usage.ContextTokens, done.LastRequestTokens);
        Assert.Equal(1, done.CommittedAnswers);
    }

    [Fact]
    public async Task AFollowUp_SendsTheCommittedConversationPlusTheQuestion_AsAValidRequest()
    {
        Guid id = Store.Create().Id;
        await _harness.AnswerAsync(id, "First?", await Replies.TextAsync());

        Task turn = _harness.AskAsync(id, "Second?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();

        Assert.Equal(3, answer.Messages.Count);
        AssertSendable(answer.Messages);
        answer.Answer(await Replies.TextAsync(write1h: 0, read: 2_080));
        await StoreHarness.WaitAsync(turn);

        ConversationSnapshot done = Store.Get(id)!;
        Assert.Equal(4, done.Messages.Count);
        Assert.Equal(2, done.CommittedAnswers);
        Assert.Equal("First?", done.Title);
    }

    [Fact]
    public async Task TheAnswerInProgress_IsInTheSnapshot_ForATabOpenedMidAnswer()
    {
        Guid id = Store.Create().Id;
        AnswerResult result = await Replies.TextAsync();
        Task turn = _harness.AskAsync(id, "Q?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();

        answer.Send(
            ScriptedAnswer.UsageOf(result),
            new AnswerEvent.ThinkingDelta("Thinking "),
            new AnswerEvent.ThinkingDelta("more."),
            new AnswerEvent.ThinkingRedacted(),
            new AnswerEvent.TextDelta(2, "Sumps hold "),
            new AnswerEvent.TextDelta(2, "5 gallons"),
            new AnswerEvent.TextBlockCompleted(2, [], [1, 2]),
            new AnswerEvent.TextBlockCompleted(3, [], []),
            new AnswerEvent.TextDelta(4, "."));
        ConversationSnapshot snapshot = await _harness.WaitForAsync(id, s => s.InProgress?.Segments.Count == 2);

        InProgressAnswer progress = snapshot.InProgress!;
        Assert.Equal("Q?", progress.Question);
        Assert.Equal(StoreHarness.Start, progress.AskedAt);
        Assert.Equal("Thinking more.", progress.Thinking);
        Assert.True(progress.HasRedactedThinking);
        Assert.Equal(result.Usage with { Output = 0 }, progress.Usage);
        Assert.False(progress.IsStopping);
        Assert.True(progress.HasText);

        // The empty, uncited block 3 adds no segment.
        Assert.Collection(
            progress.Segments,
            segment => Assert.Equal(new AnswerSegment("Sumps hold 5 gallons", [1, 2]), segment, SegmentComparer),
            segment => Assert.Equal(new AnswerSegment(".", []), segment, SegmentComparer));

        answer.Send(new AnswerEvent.Completed(result));
        await StoreHarness.WaitAsync(turn);
    }

    [Fact]
    public void ASnapshot_IsReusedUntilSomethingChanges()
    {
        Guid id = Store.Create().Id;
        ConversationSnapshot first = Store.Get(id)!;

        Assert.Same(first, Store.Get(id));
        Assert.True(Store.TryChangeSettings(id, AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Low)));
        Assert.NotSame(first, Store.Get(id));
    }

    [Fact]
    public async Task OneQuestionAtATime_IsEnforcedByTheStore()
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "First?");

        AskResult second = Store.Ask(id, "Second?");

        Assert.False(second.Accepted);
        Assert.Contains("already in progress", second.Refusal, StringComparison.Ordinal);
        Assert.True(second.Turn.IsCompleted);

        (await _harness.Answers.NextAsync()).Answer(await Replies.TextAsync());
        await StoreHarness.WaitAsync(turn);
        Assert.Single(_harness.Answers.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void AnEmptyQuestion_IsRefused(string question)
    {
        Guid id = Store.Create().Id;

        AskResult result = Store.Ask(id, question);

        Assert.Equal("The question is empty.", result.Refusal);
        Assert.Empty(_harness.Answers.Calls);
        Assert.False(Store.Get(id)!.IsAnswering);
    }

    [Fact]
    public void AQuestionForAConversationThatDoesNotExist_IsRefused() =>
        Assert.False(Store.Ask(Guid.NewGuid(), "Q?").Accepted);

    [Fact]
    public void WithNoCorpusLoaded_AnEmptyConversationCannotAsk()
    {
        using var harness = new StoreHarness(new CorpusState.Loading());
        Guid id = harness.Store.Create().Id;

        AskResult result = harness.Store.Ask(id, "Q?");

        Assert.Contains("corpus is not loaded", result.Refusal, StringComparison.Ordinal);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task AfterAReloadChangesTheCorpus_ACommittedConversation_KeepsItsSnapshot_ButTakesNoQuestions()
    {
        Guid id = Store.Create().Id;
        await _harness.AnswerAsync(id, "First?", await Replies.TextAsync());
        IReadOnlyList<ChatMessage> messages = Store.Get(id)!.Messages;
        Guid empty = Store.Create().Id;
        DocQaMs.Core.Corpus.Corpus reloaded = TestCorpus.Create("fingerprint-2");
        _harness.CorpusSource.Publish(FakeCorpusSource.Ready(reloaded));

        // Continuing would quote-check against text that is no longer current, and clearing it would lose work; so its
        // answers stay readable against the snapshot they were given, and a question is refused (plan item 3).
        AskResult refused = Store.Ask(id, "Second?");
        Assert.False(refused.Accepted);
        Assert.Equal(ConversationStore.CorpusReplacedRefusal, refused.Refusal);
        Assert.Empty(_harness.Answers.Calls.Skip(1));

        ConversationSnapshot after = Store.Get(id)!;
        Assert.False(after.IsAnswering);
        Assert.Same(messages, after.Messages);
        Assert.Same(_harness.Corpus, after.Corpus);
        Assert.All(after.Answers, record => Assert.Same(_harness.Corpus, record.Corpus));

        // A conversation with nothing committed, even one created before the reload, asks about the current corpus.
        ConversationSnapshot other = await _harness.AnswerAsync(empty, "About the new corpus?", await Replies.TextAsync());
        Assert.Same(reloaded, other.Corpus);
    }

    [Fact]
    public async Task ACommittedConversation_CanAskWhileTheCorpusIsReloading()
    {
        Guid id = Store.Create().Id;
        await _harness.AnswerAsync(id, "First?", await Replies.TextAsync());
        _harness.CorpusSource.Publish(new CorpusState.Loading());

        ConversationSnapshot done = await _harness.AnswerAsync(id, "Second?", await Replies.TextAsync(write1h: 0, read: 2_080));

        Assert.Equal(4, done.Messages.Count);
    }

    [Fact]
    public async Task AReloadThatFindsTheSameCorpus_LeavesTheConversationOpen()
    {
        Guid id = Store.Create().Id;
        await _harness.AnswerAsync(id, "First?", await Replies.TextAsync());

        _harness.CorpusSource.Publish(FakeCorpusSource.Ready(TestCorpus.Create()));

        await _harness.AnswerAsync(id, "Second?", await Replies.TextAsync(write1h: 0, read: 2_080));
        Assert.Equal(2, Store.Get(id)!.CommittedAnswers);
    }

    // --- What is not committed ----------------------------------------------------------------------

    [Fact]
    public async Task AReplyThatCannotBeReplayed_IsNotCommitted_AndItsQuestionIsReturned()
    {
        Guid id = Store.Create().Id;
        AnswerResult refusal = await Replies.EmptyRefusalAsync();
        Assert.Empty(ReplayConverter.ToReplay(refusal.Reply));

        ConversationSnapshot snapshot = await _harness.AnswerAsync(id, "Q?", refusal);

        Assert.Empty(snapshot.Messages);
        Assert.Null(snapshot.Corpus);
        AnswerRecord record = Assert.Single(snapshot.Answers);
        Assert.Equal(TurnOutcome.NotReplayable, record.Outcome);
        Assert.False(record.IsCommitted);
        Assert.Equal(ConversationStore.NotReplayableNotice, record.Notice);
        Assert.Contains(record.Warnings, warning => warning.Kind == TurnWarningKind.Refused);
        Assert.Same(refusal, record.Result);
        Assert.Equal("Q?", snapshot.ReturnedQuestion);
        Assert.Equal(TurnOutcome.NotReplayable, snapshot.LastOutcome);
        Assert.False(snapshot.SettingsLocked);

        // It was paid for, so it counts.
        Assert.Equal(refusal.Cost, snapshot.Cost.Known);

        // The next question is a first question again, rebuilt from the corpus; asking clears the returned one.
        Task next = _harness.AskAsync(id, "Again?");
        ScriptedAnswer retry = await _harness.Answers.NextAsync();
        Assert.Single(retry.Messages);
        Assert.Null(Store.Get(id)!.ReturnedQuestion);
        retry.Answer(await Replies.TextAsync());
        await StoreHarness.WaitAsync(next);
    }

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task AfterAFailure_TheConversationIsUnchanged_AndItsNextTurnIsValid(AnswerFailureKind kind)
    {
        Guid id = Store.Create().Id;
        ConversationSnapshot before = await _harness.AnswerAsync(id, "First?", await Replies.TextAsync());

        Task turn = _harness.AskAsync(id, "Second?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();
        answer.Send(new AnswerEvent.TextDelta(0, "Partial"));
        answer.Fail(new AnswerFailedException(kind, "It failed.", null));
        await StoreHarness.WaitAsync(turn);

        ConversationSnapshot after = Store.Get(id)!;
        Assert.Same(before.Messages, after.Messages);
        AnswerRecord failed = after.Answers[^1];
        Assert.Equal(TurnOutcome.Failed, failed.Outcome);
        Assert.Equal(kind, failed.FailureKind);
        Assert.Equal("It failed." + ConversationStore.NotAddedSuffix, failed.Notice);
        Assert.Equal("Partial", Assert.Single(failed.Segments).Text);
        Assert.Null(failed.Result);
        Assert.Null(failed.Quotes);
        Assert.Null(after.InProgress);
        Assert.Equal("Second?", after.ReturnedQuestion);
        Assert.Equal(TurnOutcome.Failed, after.LastOutcome);

        // It failed before its usage arrived, on an entry the first turn had just warmed.
        decimal warmRead = _harness.Costs.Estimate(AnswerSettings.Default, _harness.Corpus).WarmInput;
        Assert.Null(failed.Cost);
        Assert.Equal(FormattableString.Invariant($"Cost unknown: up to about ${warmRead:0.00} if the request had started."), failed.CostNote);
        Assert.Equal(1, after.Cost.UnknownCostTurns);

        Task next = _harness.AskAsync(id, "Third?");
        ScriptedAnswer retry = await _harness.Answers.NextAsync();
        Assert.Equal(3, retry.Messages.Count);
        AssertSendable(retry.Messages);
        retry.Answer(await Replies.TextAsync(write1h: 0, read: 2_080));
        await StoreHarness.WaitAsync(next);
        Assert.Equal(4, Store.Get(id)!.Messages.Count);
    }

    [Fact]
    public async Task AnUnexpectedException_FailsTheTurn_AndIsLogged()
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        (await _harness.Answers.NextAsync()).Fail(new InvalidOperationException("Something odd."));
        await StoreHarness.WaitAsync(turn);

        AnswerRecord failed = Assert.Single(Store.Get(id)!.Answers);
        Assert.Equal(AnswerFailureKind.Unexpected, failed.FailureKind);
        Assert.Equal(ConversationStore.UnexpectedFailure + ConversationStore.NotAddedSuffix, failed.Notice);
        Assert.Contains(_harness.Logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task AStreamThatEndsWithoutAFinalEvent_FailsTheTurn()
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        (await _harness.Answers.NextAsync()).End();
        await StoreHarness.WaitAsync(turn);

        AnswerRecord failed = Assert.Single(Store.Get(id)!.Answers);
        Assert.Equal(TurnOutcome.Failed, failed.Outcome);
        Assert.Contains("without a final message", failed.Notice, StringComparison.Ordinal);
        Assert.Empty(Store.Get(id)!.Messages);
    }

    [Fact]
    public async Task AMissingKey_IsKnownToHaveCostNothing()
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        (await _harness.Answers.NextAsync()).Fail(
            new AnswerFailedException(AnswerFailureKind.Credentials, "Cannot load the API key: file not found.", null));
        await StoreHarness.WaitAsync(turn);

        AnswerRecord failed = Assert.Single(Store.Get(id)!.Answers);
        Assert.Equal(TurnCost.Zero, failed.Cost);
        Assert.Equal(ConversationStore.NothingSentNote, failed.CostNote);
        Assert.True(Store.Get(id)!.Cost.IsComplete);
    }

    [Fact]
    public async Task AFailureAfterTheUsageArrived_RecordsTheInputCost_AndLeavesTheEntryWarm()
    {
        Guid id = Store.Create().Id;
        CorpusCacheKey key = CorpusCacheKey.For(_harness.Corpus, AnswerSettings.Default);
        AnswerEvent.UsageStarted usage = ScriptedAnswer.UsageOf(await Replies.TextAsync());
        Task turn = _harness.AskAsync(id, "Q?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();

        answer.Send(usage, new AnswerEvent.TextDelta(0, "Partial"));
        answer.Fail(new AnswerFailedException(AnswerFailureKind.Connection, "Network error.", null));
        await StoreHarness.WaitAsync(turn);

        ConversationSnapshot after = Store.Get(id)!;
        AnswerRecord failed = Assert.Single(after.Answers);
        Assert.Equal(TurnOutcome.Failed, failed.Outcome);
        Assert.Equal(usage.Cost, failed.Cost);
        Assert.Equal(
            FormattableString.Invariant($"Input billed about ${usage.Cost.Total:0.00}; output before the failure not reported."),
            failed.CostNote);
        Assert.Equal(new ConversationCost(usage.Cost, 0, 1), after.Cost);
        Assert.True(_harness.Warmth.GetWarmth(key).IsProbablyWarm);
        Assert.Empty(after.Messages);

        // The next question is a first question again, and valid.
        Task next = _harness.AskAsync(id, "Again?");
        ScriptedAnswer retry = await _harness.Answers.NextAsync();
        Assert.Single(retry.Messages);
        AssertSendable(retry.Messages);
        retry.Answer(await Replies.TextAsync(write1h: 0, read: 2_080));
        await StoreHarness.WaitAsync(next);
    }

    // --- Settings -----------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_AreEditableBeforeTheFirstQuestion_AndLockedInFlightAndOnceATurnIsCommitted()
    {
        AnswerSettings sonnetLow = AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Low);
        Guid id = Store.Create().Id;
        Assert.False(Store.Get(id)!.SettingsLocked);
        Assert.True(Store.TryChangeSettings(id, sonnetLow));
        Assert.Equal(sonnetLow, Store.Get(id)!.Settings);

        Task turn = _harness.AskAsync(id, "Q?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();
        Assert.Equal(sonnetLow, answer.Settings);
        Assert.True(Store.Get(id)!.SettingsLocked);
        Assert.False(Store.TryChangeSettings(id, AnswerSettings.Default));

        answer.Answer(await Replies.TextAsync(settings: sonnetLow));
        await StoreHarness.WaitAsync(turn);

        ConversationSnapshot done = Store.Get(id)!;
        Assert.True(done.SettingsLocked);
        Assert.False(Store.TryChangeSettings(id, AnswerSettings.Default));
        Assert.Equal(sonnetLow, done.Settings);
        Assert.Equal(sonnetLow, done.Answers[0].Settings);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("failed")]
    [InlineData("not replayable")]
    public async Task Settings_AreEditableAgain_AfterAFirstTurnThatWasNotCommitted(string ending)
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();

        switch (ending)
        {
            case "stopped":
                Store.Stop(id);
                break;
            case "failed":
                answer.Fail(new AnswerFailedException(AnswerFailureKind.Connection, "Network error.", null));
                break;
            default:
                answer.Answer(await Replies.EmptyRefusalAsync());
                break;
        }

        await StoreHarness.WaitAsync(turn);

        Assert.False(Store.Get(id)!.SettingsLocked);
        Assert.True(Store.TryChangeSettings(id, AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Max)));
    }

    [Fact]
    public void ANewConversation_TakesTheGivenSettings_OrOpusAtHighOnFirstRun()
    {
        ConversationSnapshot firstRun = Store.Create();
        ConversationSnapshot active = Store.Create(AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Medium));

        ConversationSnapshot copied = Store.Create(active.Settings);

        Assert.Equal(AnswerSettings.Default, firstRun.Settings);
        Assert.Equal(active.Settings, copied.Settings);
        Assert.NotEqual(active.Id, copied.Id);
        Assert.Equal(ConversationStore.NewConversationTitle, copied.Title);
        Assert.Empty(copied.Messages);
    }

    // --- Stop ---------------------------------------------------------------------------------------

    [Fact]
    public async Task StopDuringAFollowUp_KeepsThePartialAnswer_LeavesTheConversationIntact_AndRecordsTheInputCost()
    {
        Guid id = Store.Create().Id;
        ConversationSnapshot before = await _harness.AnswerAsync(id, "First?", await Replies.TextAsync());
        AnswerResult warm = await Replies.TextAsync(write1h: 0, read: 2_080);

        Task turn = _harness.AskAsync(id, "Second?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();
        AnswerEvent.UsageStarted usage = ScriptedAnswer.UsageOf(warm);
        answer.Send(usage, new AnswerEvent.ThinkingDelta("Looking."), new AnswerEvent.TextDelta(1, "Sumps must"));
        await _harness.WaitForAsync(id, s => s.InProgress?.HasText == true);

        Store.Stop(id);
        await StoreHarness.WaitAsync(turn);

        ConversationSnapshot after = Store.Get(id)!;
        Assert.Same(before.Messages, after.Messages);
        AnswerRecord stopped = after.Answers[^1];
        Assert.Equal(TurnOutcome.Stopped, stopped.Outcome);
        Assert.Equal(ConversationStore.StoppedNotice, stopped.Notice);
        Assert.Equal("Sumps must", Assert.Single(stopped.Segments).Text);
        Assert.Equal("Looking.", stopped.Thinking);
        Assert.Null(stopped.Result);
        Assert.Null(stopped.Quotes);
        Assert.Equal(usage.Cost, stopped.Cost);
        Assert.Equal(
            FormattableString.Invariant($"Input billed about ${usage.Cost.Total:0.00}; output before Stop not reported."),
            stopped.CostNote);
        Assert.Equal(new ConversationCost(before.Cost.Known.Add(usage.Cost), 0, 1), after.Cost);
        Assert.False(after.Cost.IsComplete);
        Assert.Equal("Second?", after.ReturnedQuestion);
        Assert.Equal(TurnOutcome.Stopped, after.LastOutcome);
        Assert.True(answer.Token.IsCancellationRequested);
        await answer.Closed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Task next = _harness.AskAsync(id, "Third?");
        ScriptedAnswer retry = await _harness.Answers.NextAsync();
        Assert.Equal(3, retry.Messages.Count);
        AssertSendable(retry.Messages);
        retry.Answer(warm);
        await StoreHarness.WaitAsync(next);
    }

    [Fact]
    public async Task StopBeforeTheUsageArrives_RecordsTheCostAsUnknown_UpToTheColdWrite()
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        await _harness.Answers.NextAsync();

        Store.Stop(id);
        await StoreHarness.WaitAsync(turn);

        AnswerRecord stopped = Assert.Single(Store.Get(id)!.Answers);
        Assert.Null(stopped.Cost);

        // Nothing had used the entry, so the request would have written the corpus.
        decimal coldWrite = TestCorpus.Tokens * _harness.Options.Prices[ModelIds.ClaudeOpus5].CacheWrite1hPerMTok / 1_000_000m;
        Assert.Equal(FormattableString.Invariant($"Cost unknown: up to about ${coldWrite:0.00} if the request had started."), stopped.CostNote);
        Assert.Equal(new ConversationCost(TurnCost.Zero, 1, 0), Store.Get(id)!.Cost);
        Assert.Equal(1, Store.TotalCost.UnknownCostTurns);
        Assert.False(Store.TotalCost.IsComplete);
    }

    [Fact]
    public async Task AStopLandingBetweenTheFinalEventAndTheCommit_ChangesNothing()
    {
        Guid id = Store.Create().Id;
        Store.BeforeCommit = Store.Stop;
        AnswerResult result = await Replies.TextAsync();

        ConversationSnapshot done = await _harness.AnswerAsync(id, "Q?", result);

        Assert.Equal(TurnOutcome.Completed, done.LastOutcome);
        Assert.Equal(2, done.Messages.Count);
        Assert.Same(result, Assert.Single(done.Answers).Result);
        Assert.Null(done.ReturnedQuestion);

        // The Stop did land: it found the turn and cancelled its token.
        Assert.True(_harness.Answers.Calls[0].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ASecondStop_AndAStopAfterTheTurnEnded_DoNothing()
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        await _harness.Answers.NextAsync();

        Store.Stop(id);
        Store.Stop(id);
        await StoreHarness.WaitAsync(turn);

        ConversationSnapshot ended = Store.Get(id)!;
        Assert.Single(ended.Answers);
        Store.Stop(id);
        Store.Stop(Guid.NewGuid());
        Assert.Same(ended, Store.Get(id));

        Guid other = Store.Create().Id;
        ConversationSnapshot completed = await _harness.AnswerAsync(other, "Q?", await Replies.TextAsync());
        Store.Stop(other);
        Assert.Same(completed, Store.Get(other));
    }

    [Fact]
    public async Task Stop_ReturnsAtOnce_WhileTheStreamIsStillBlocked()
    {
        _harness.Answers.IgnoreCancellation = true;
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();

        Store.Stop(id);

        // The stream has not noticed yet, and Stop did not wait for it to.
        Assert.False(turn.IsCompleted);
        Assert.True(answer.Token.IsCancellationRequested);
        ConversationSnapshot stopping = Store.Get(id)!;
        Assert.True(stopping.InProgress!.IsStopping);
        Assert.True(stopping.SettingsLocked);

        answer.Fail(new OperationCanceledException(answer.Token));
        await StoreHarness.WaitAsync(turn);
        Assert.Equal(TurnOutcome.Stopped, Store.Get(id)!.LastOutcome);
    }

    [Fact]
    public async Task TheReturnedQuestion_SurvivesADisposedSubscriber_AndIsTakenOnce()
    {
        Guid id = Store.Create().Id;
        Store.Changed += _ => throw new ObjectDisposedException("a closed tab's component");
        Task turn = _harness.AskAsync(id, "What about sumps?");
        await _harness.Answers.NextAsync();

        Store.Stop(id);
        await StoreHarness.WaitAsync(turn);

        Assert.Equal("What about sumps?", Store.Get(id)!.ReturnedQuestion);
        Assert.Equal("What about sumps?", Store.TakeReturnedQuestion(id));
        Assert.Null(Store.TakeReturnedQuestion(id));
        Assert.Null(Store.Get(id)!.ReturnedQuestion);
        Assert.Contains(_harness.Logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is ObjectDisposedException);
    }

    [Fact]
    public async Task AThrowingSubscriber_DoesNotStopATurnFromCommitting_OrTheOtherSubscribers()
    {
        Guid id = Store.Create().Id;
        int notified = 0;
        Store.Changed += _ => throw new InvalidOperationException("A broken component.");
        Store.Changed += changed =>
        {
            if (changed == id)
            {
                Interlocked.Increment(ref notified);
            }
        };

        ConversationSnapshot done = await _harness.AnswerAsync(id, "Q?", await Replies.TextAsync());

        Assert.Equal(TurnOutcome.Completed, done.LastOutcome);

        // At least the question going out and the commit.
        Assert.True(Volatile.Read(ref notified) >= 2);
    }

    [Fact]
    public async Task Shutdown_StopsTheTurnInFlight_AndRefusesNewQuestions()
    {
        Guid id = Store.Create().Id;
        Task turn = _harness.AskAsync(id, "Q?");
        await _harness.Answers.NextAsync();

        Store.Dispose();
        await StoreHarness.WaitAsync(turn);

        Assert.Equal(TurnOutcome.Stopped, Store.Get(id)!.LastOutcome);
        Assert.Equal("The app is shutting down.", Store.Ask(id, "Again?").Refusal);
    }

    // --- The corpus cache ---------------------------------------------------------------------------

    [Fact]
    public async Task TheEntry_IsMarkedAsBeingWritten_UntilTheUsageArrives_AndStaysWarmAfterAStop()
    {
        Guid id = Store.Create().Id;
        CorpusCacheKey key = CorpusCacheKey.For(_harness.Corpus, AnswerSettings.Default);
        Task turn = _harness.AskAsync(id, "Q?");
        ScriptedAnswer answer = await _harness.Answers.NextAsync();
        Assert.Equal(CacheWarmthStatus.ColdWriteInProgress, _harness.Warmth.GetWarmth(key).Status);

        _harness.Time.Advance(TimeSpan.FromSeconds(8));
        answer.Send(ScriptedAnswer.UsageOf(await Replies.TextAsync()));
        await _harness.WaitForAsync(id, s => s.InProgress?.Usage is not null);

        Assert.Equal(
            new CacheWarmth(CacheWarmthStatus.ProbablyWarm, StoreHarness.Start + TimeSpan.FromSeconds(8) + TimeSpan.FromMinutes(55)),
            _harness.Warmth.GetWarmth(key));

        Store.Stop(id);
        await StoreHarness.WaitAsync(turn);
        Assert.True(_harness.Warmth.GetWarmth(key).IsProbablyWarm);
        Assert.Equal(CacheWarmthStatus.Cold, _harness.Warmth.GetWarmth(key with { Effort = EffortLevel.Low }).Status);
    }

    [Fact]
    public async Task AFailureBeforeTheUsageArrives_ClearsTheWriteInProgress()
    {
        Guid id = Store.Create().Id;
        CorpusCacheKey key = CorpusCacheKey.For(_harness.Corpus, AnswerSettings.Default);
        Task turn = _harness.AskAsync(id, "Q?");
        (await _harness.Answers.NextAsync()).Fail(new AnswerFailedException(AnswerFailureKind.Connection, "Network error.", null));
        await StoreHarness.WaitAsync(turn);

        Assert.Equal(CacheWarmthStatus.Cold, _harness.Warmth.GetWarmth(key).Status);
    }

    [Fact]
    public async Task AnExpectedColdWrite_IsReportedAsAColdWrite_AndAnUnexpectedOneAsAMiss()
    {
        AnswerResult cold = await Replies.TextAsync(write1h: 2_080, read: 0);
        ConversationSnapshot first = await _harness.AnswerAsync(Store.Create().Id, "Q1?", cold);
        Assert.Equal(CacheStatus.ColdWrite, first.Answers[0].Cache!.Status);

        // Two hours later the tracker predicts a cold write, so writing the corpus again is expected.
        _harness.Time.Advance(TimeSpan.FromHours(2));
        ConversationSnapshot second = await _harness.AnswerAsync(Store.Create().Id, "Q2?", cold);
        Assert.Equal(CacheStatus.ColdWrite, second.Answers[0].Cache!.Status);
        Assert.DoesNotContain(second.Answers[0].Warnings, warning => warning.Kind == TurnWarningKind.CacheMiss);

        // Within the hour it predicts a read, so the same write is a miss.
        ConversationSnapshot third = await _harness.AnswerAsync(Store.Create().Id, "Q3?", cold);
        CacheReport miss = third.Answers[0].Cache!;
        Assert.Equal(CacheStatus.Miss, miss.Status);
        Assert.Equal(CacheMissKind.Expired, miss.MissKind);
        Assert.Equal(miss.Warning, Assert.Single(third.Answers[0].Warnings, warning => warning.Kind == TurnWarningKind.CacheMiss).Message);
        Assert.Contains(_harness.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("CACHE MISS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReloadThatChangesTheFingerprint_DropsTheOldBaselines()
    {
        CorpusCacheKey key = CorpusCacheKey.For(_harness.Corpus, AnswerSettings.Default);
        await _harness.AnswerAsync(Store.Create().Id, "Q?", await Replies.TextAsync());
        Assert.Equal(2_080, _harness.Baselines.Get(key));

        _harness.CorpusSource.Publish(FakeCorpusSource.Ready(TestCorpus.Create()));
        Assert.Equal(2_080, _harness.Baselines.Get(key));

        _harness.CorpusSource.Publish(FakeCorpusSource.Ready(TestCorpus.Create("fingerprint-2")));
        Assert.Null(_harness.Baselines.Get(key));
    }

    [Fact]
    public async Task ARequestAtTheContextThreshold_IsWarned_AndOneBelowItIsNot()
    {
        long limit = (long)(_harness.Options.ContextWindow * _harness.Options.ContextWarnFraction);

        // With the 2,080-token write and the 20-token tail, each request is exactly this size.
        ConversationSnapshot below = await _harness.AnswerAsync(Store.Create().Id, "Q?", await Replies.TextAsync(input: limit - 2_101));
        ConversationSnapshot at = await _harness.AnswerAsync(Store.Create().Id, "Q?", await Replies.TextAsync(input: limit - 2_100));

        Assert.DoesNotContain(below.Answers[0].Warnings, warning => warning.Kind == TurnWarningKind.Context);
        TurnWarning context = Assert.Single(at.Answers[0].Warnings, warning => warning.Kind == TurnWarningKind.Context);
        Assert.StartsWith(
            FormattableString.Invariant($"CONTEXT: this request was {limit:N0} tokens, past 70% of the {_harness.Options.ContextWindow:N0}-token window."),
            context.Message,
            StringComparison.Ordinal);
        Assert.Equal(limit, at.LastRequestTokens);
    }

    // --- Through the real answer service ------------------------------------------------------------

    [Fact]
    public async Task ThroughTheRealAnswerService_ACannedAnswerIsCommitted_AndTheFollowUpCarriesItBack()
    {
        using var answers = new AnswerHarness(CannedAnswer.Updates);
        using var harness = new StoreHarness(answers: answers.Service);
        Guid id = harness.Store.Create().Id;

        await StoreHarness.WaitAsync(harness.AskAsync(id, "What must a sump hold?"));
        await StoreHarness.WaitAsync(harness.AskAsync(id, "And a spill bucket?"));

        ConversationSnapshot done = harness.Store.Get(id)!;
        Assert.All(done.Answers, record => Assert.Equal(TurnOutcome.Completed, record.Outcome));
        Assert.Equal(4, done.Messages.Count);
        AnswerRecord first = done.Answers[0];
        Assert.Equal("Sumps must hold a minimum of 5 gallons.", string.Concat(first.Segments.Select(segment => segment.Text)));
        Assert.Equal([1, 2], first.Segments[1].SourceNumbers);
        Assert.Equal(CannedAnswer.Thinking, first.Thinking);
        Assert.True(first.HasRedactedThinking);
        Assert.Equal(2, first.Quotes!.Checks.Count);
        Assert.Equal(CacheStatus.ColdWrite, first.Cache!.Status);

        // The canned response writes the corpus again on the follow-up, which the tracker did not predict.
        Assert.Equal(CacheStatus.Miss, done.Answers[1].Cache!.Status);

        Assert.Equal(2, answers.Client.Calls.Count);
        IReadOnlyList<ChatMessage> messages = answers.Client.Calls[1].Messages;
        Assert.Equal(3, messages.Count);
        TextContent previousQuestion = Assert.IsType<TextContent>(messages[0].Contents[^1]);
        Assert.Equal("What must a sump hold?", previousQuestion.Text);
        Assert.Equal("5m", previousQuestion.AdditionalProperties?[ChatKeys.CacheTtl]);

        // The reply goes back as committed: its thinking, redacted thinking and cited text, minus the empty block.
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Same(done.Messages[1], messages[1]);
        Assert.Collection(
            messages[1].Contents,
            content => Assert.Equal(CannedAnswer.Signature, Assert.IsType<TextReasoningContent>(content).ProtectedData),
            content => Assert.Equal(CannedAnswer.RedactedData, Assert.IsType<TextReasoningContent>(content).ProtectedData),
            content => Assert.Equal("Sumps must hold ", Assert.IsType<TextContent>(content).Text),
            content => Assert.Equal(2, Assert.IsType<TextContent>(content).Annotations?.Count),
            content => Assert.Equal(".", Assert.IsType<TextContent>(content).Text));
        Assert.Equal("And a spill bucket?", Assert.IsType<TextContent>(Assert.Single(messages[2].Contents)).Text);
    }

    // --- Titles and totals --------------------------------------------------------------------------

    [Theory]
    [InlineData(null, ConversationStore.NewConversationTitle)]
    [InlineData("   ", ConversationStore.NewConversationTitle)]
    [InlineData("  What   must\na sump\thold? ", "What must a sump hold?")]
    public void TheTitle_IsTheQuestionOnOneLine(string? question, string expected) =>
        Assert.Equal(expected, ConversationStore.TitleOf(question));

    [Fact]
    public void ALongTitle_IsShortenedToSixtyCharacters_WithoutSplittingACharacter()
    {
        string ellipsis = ((char)0x2026).ToString();

        string plain = ConversationStore.TitleOf(new string('a', 100));
        string split = ConversationStore.TitleOf(new string('a', 58) + SpikeFixtures.NonBmp + new string('b', 40));

        Assert.Equal(60, plain.Length);
        Assert.EndsWith(ellipsis, plain, StringComparison.Ordinal);
        Assert.Equal(new string('a', 58) + ellipsis, split);
    }

    [Fact]
    public async Task List_IsNewestFirst_AndTheTotalCostSumsEveryConversation()
    {
        Guid older = Store.Create().Id;
        Guid newer = Store.Create().Id;
        AnswerResult cold = await Replies.TextAsync();
        AnswerResult warm = await Replies.TextAsync(write1h: 0, read: 2_080);

        await _harness.AnswerAsync(older, "A?", cold);
        await _harness.AnswerAsync(newer, "B?", warm);

        Assert.Equal([newer, older], Store.List().Select(snapshot => snapshot.Id));
        Assert.Equal(cold.Cost.Add(warm.Cost), Store.TotalCost.Known);
        Assert.True(Store.TotalCost.IsComplete);
    }

    /// <summary>Asserts roles alternate from a user message to a final user message, and the request builder accepts them.</summary>
    private static void AssertSendable(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            Assert.Equal(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, messages[i].Role);
        }

        Assert.Equal(ChatRole.User, messages[^1].Role);
        MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, EffortLevel.High, 32_000, CacheAffecting.SystemPrompt, messages);
    }

    private static readonly IEqualityComparer<AnswerSegment> SegmentComparer = EqualityComparer<AnswerSegment>.Create(
        (x, y) => x!.Text == y!.Text && x.SourceNumbers.SequenceEqual(y.SourceNumbers),
        segment => segment.Text.GetHashCode(StringComparison.Ordinal));
}
