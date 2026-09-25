using System.Text;
using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Conversations;

/// <summary>
/// Holds every conversation in memory for the app's lifetime, runs their turns, and publishes immutable
/// snapshots. Ported from the prototype's <c>repl.py</c> <c>Session</c> wherever the prototype learned
/// something the hard way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Commit only after success, and only if the reply can be replayed.</b> A user message left without a reply
/// makes the next request send two user messages in a row, which the API rejects: a conversation broken by a
/// network blip several questions earlier. So each request is built from the committed messages plus the new
/// question, and both are committed only after the answer completes, and only if the reply still has content
/// once empty text blocks are dropped.
/// </para>
/// <para>
/// <b>No lock is held across I/O.</b> Each conversation has a short lock that covers state changes and
/// snapshots, never the stream. <see cref="Stop"/> takes it only to read the turn's token source, then cancels
/// outside it, so it is immediate even in the middle of a long <c>max</c> answer. Once the final event has
/// arrived, the turn stops watching its token: the commit cannot be cancelled.
/// </para>
/// <para>
/// <b>A turn belongs to the conversation, not to the page.</b> It runs on its own task, with its own token source
/// that only Stop and app shutdown cancel, and the whole turn is wrapped in catch-and-log.
/// </para>
/// </remarks>
public sealed class ConversationStore : IConversationStore, IDisposable
{
    internal const string StoppedNotice = "Stopped: not checked, not added to the conversation.";

    internal const string NotAddedSuffix = " Not added to the conversation.";

    internal const string NotReplayableNotice =
        "Not added to the conversation: the answer carried no text that can be sent back, so the question was " +
        "returned to the input.";

    internal const string UnexpectedFailure = "The answer failed unexpectedly. See the log.";

    internal const string NothingSentNote = "Nothing was sent, so nothing was billed.";

    internal const string NewConversationTitle = "New conversation";

    internal const string CorpusReplacedRefusal =
        "The corpus was reloaded and has changed since this conversation began, so its answers are checked against " +
        "text that is no longer current. Start a new conversation to ask about the current corpus.";

    private const int TitleLength = 60;

    private readonly IAnswerService _answers;
    private readonly QuoteChecker _quotes;
    private readonly ICorpusSource _corpus;
    private readonly CacheWarmthTracker _warmth;
    private readonly CacheBaselines _baselines;
    private readonly CostEstimator _costs;
    private readonly DocQaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConversationStore> _logger;

    // Cancelled when the app stops, never disposed: a turn still in flight may be linking to its token.
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _stoppingRegistration;

    // Guards the conversation list and the current fingerprint; each conversation has its own lock.
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Conversation> _conversations = [];
    private readonly List<Conversation> _order = [];
    private string? _currentFingerprint;

    /// <summary>Creates the store.</summary>
    /// <param name="answers">Streams each answer.</param>
    /// <param name="quotes">Checks each completed answer's quotes.</param>
    /// <param name="corpus">The current corpus, which a conversation's first question is built from.</param>
    /// <param name="warmth">Predicts cold corpus writes, and records each request's cache activity.</param>
    /// <param name="baselines">Checks each completed turn's cache read against its entry's baseline.</param>
    /// <param name="costs">Estimates the cost of a turn stopped before its usage arrived.</param>
    /// <param name="options">The context window, its warning threshold, and the prices.</param>
    /// <param name="time">Stamps each conversation and question.</param>
    /// <param name="logger">Logs every failure, and every subscriber that throws.</param>
    /// <param name="lifetime">
    /// Cancels every turn in flight when the app stops; <see langword="null"/> outside a host, where
    /// <see cref="Dispose"/> does it.
    /// </param>
    public ConversationStore(
        IAnswerService answers,
        QuoteChecker quotes,
        ICorpusSource corpus,
        CacheWarmthTracker warmth,
        CacheBaselines baselines,
        CostEstimator costs,
        IOptions<DocQaOptions> options,
        TimeProvider time,
        ILogger<ConversationStore> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(warmth);
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _answers = answers;
        _quotes = quotes;
        _corpus = corpus;
        _warmth = warmth;
        _baselines = baselines;
        _costs = costs;
        _options = options.Value;
        _time = time;
        _logger = logger;

        _currentFingerprint = (corpus.State as CorpusState.Ready)?.Corpus.Fingerprint;
        _corpus.StateChanged += OnCorpusChanged;
        if (lifetime is not null)
        {
            _stoppingRegistration = lifetime.ApplicationStopping.Register(
                static state => ((CancellationTokenSource)state!).Cancel(), _shutdown);
        }
    }

    /// <inheritdoc/>
    public event Action<Guid>? Changed;

    /// <inheritdoc/>
    public ConversationCost TotalCost
    {
        get
        {
            ConversationCost total = ConversationCost.Zero;
            foreach (Conversation conversation in All())
            {
                lock (conversation.Gate)
                {
                    total = total.Add(conversation.Cost);
                }
            }

            return total;
        }
    }

    /// <summary>
    /// Test hook: runs after a turn's final event has arrived and before it is committed, the window in which a
    /// late Stop must change nothing.
    /// </summary>
    internal Action<Guid>? BeforeCommit { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<ConversationSnapshot> List()
    {
        List<ConversationSnapshot> snapshots = [];
        foreach (Conversation conversation in All().Reverse())
        {
            lock (conversation.Gate)
            {
                snapshots.Add(conversation.Snapshot());
            }
        }

        return snapshots.AsReadOnly();
    }

    /// <inheritdoc/>
    public ConversationSnapshot? Get(Guid id)
    {
        if (Find(id) is not { } conversation)
        {
            return null;
        }

        lock (conversation.Gate)
        {
            return conversation.Snapshot();
        }
    }

    /// <inheritdoc/>
    public ConversationSnapshot Create(AnswerSettings? settings = null)
    {
        var conversation = new Conversation(Guid.NewGuid(), _time.GetUtcNow(), settings ?? AnswerSettings.Default);
        lock (_gate)
        {
            _conversations.Add(conversation.Id, conversation);
            _order.Add(conversation);
        }

        ConversationSnapshot snapshot;
        lock (conversation.Gate)
        {
            snapshot = conversation.Snapshot();
        }

        Raise(conversation.Id);
        return snapshot;
    }

    /// <inheritdoc/>
    public bool TryChangeSettings(Guid id, AnswerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Find(id) is not { } conversation)
        {
            return false;
        }

        lock (conversation.Gate)
        {
            if (conversation.SettingsLocked)
            {
                return false;
            }

            if (conversation.Settings == settings)
            {
                return true;
            }

            conversation.Settings = settings;
            conversation.Touch();
        }

        Raise(id);
        return true;
    }

    /// <inheritdoc/>
    public AskResult Ask(Guid id, string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        string text = question.Trim();
        if (text.Length == 0)
        {
            return AskResult.Refused("The question is empty.");
        }

        if (Find(id) is not { } conversation)
        {
            return AskResult.Refused("That conversation no longer exists.");
        }

        Turn turn;
        lock (conversation.Gate)
        {
            if (_shutdown.IsCancellationRequested)
            {
                return AskResult.Refused("The app is shutting down.");
            }

            // Enforced here as well as by the UI: another tab may be showing the same conversation.
            if (conversation.InFlight is not null)
            {
                return AskResult.Refused("An answer is already in progress in this conversation. Wait for it, or stop it.");
            }

            // A conversation keeps the snapshot its committed turns were built from. Until then, each question
            // uses the corpus current when it is asked.
            CorpusSnapshot? current = (_corpus.State as CorpusState.Ready)?.Corpus;
            CorpusSnapshot? corpus = conversation.Corpus ?? current;
            if (corpus is null)
            {
                return AskResult.Refused("The corpus is not loaded, so there is nothing to ask about yet.");
            }

            // After a reload changes the corpus, continuing would quote-check against text that is no longer
            // current, and clearing the conversation would lose work; so it stays readable but takes no questions.
            if (current is not null && corpus.Fingerprint != current.Fingerprint)
            {
                return AskResult.Refused(CorpusReplacedRefusal);
            }

            // messages[0] is rebuilt from the corpus, never sliced from an earlier conversation, so no earlier
            // first question can carry over and steer the answers.
            MessageParam userMessage = conversation.Messages.Count == 0
                ? MessageRequestBuilder.FirstTurn(corpus.RequestDocuments, text)
                : MessageRequestBuilder.FollowUpTurn(text);

            turn = new Turn
            {
                Question = text,
                Settings = conversation.Settings,
                Corpus = corpus,
                UserMessage = userMessage,
                Request = [.. conversation.Messages, userMessage],
                AskedAt = _time.GetUtcNow(),
                Cache = _warmth.BeginRequest(CorpusCacheKey.For(corpus, conversation.Settings)),
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token),
            };
            conversation.InFlight = turn;
            conversation.ReturnedQuestion = null;
            conversation.Touch();
        }

        Raise(id);
        return AskResult.Started(Task.Run(() => RunTurnAsync(conversation, turn), CancellationToken.None));
    }

    /// <inheritdoc/>
    public void Stop(Guid id)
    {
        if (Find(id) is not { } conversation)
        {
            return;
        }

        CancellationTokenSource cancellation;
        lock (conversation.Gate)
        {
            if (conversation.InFlight is not { StopRequested: false } turn)
            {
                return;
            }

            turn.StopRequested = true;
            cancellation = turn.Cancellation;
            conversation.Touch();
        }

        try
        {
            // CancelAsync marks the token cancelled at once and runs its callbacks on the thread pool, so Stop
            // never waits for the stream to close.
            _ = cancellation.CancelAsync().ContinueWith(
                task => _logger.LogError(task.Exception, "Cancelling a turn on Stop threw."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // The turn finished between reading its token source and cancelling it: nothing to stop.
        }

        Raise(id);
    }

    /// <inheritdoc/>
    public string? TakeReturnedQuestion(Guid id)
    {
        if (Find(id) is not { } conversation)
        {
            return null;
        }

        string? question;
        lock (conversation.Gate)
        {
            question = conversation.ReturnedQuestion;
            if (question is null)
            {
                return null;
            }

            conversation.ReturnedQuestion = null;
            conversation.Touch();
        }

        Raise(id);
        return question;
    }

    /// <summary>Stops every turn in flight. The app is shutting down.</summary>
    public void Dispose()
    {
        _corpus.StateChanged -= OnCorpusChanged;
        _stoppingRegistration.Dispose();
        _shutdown.Cancel();
    }

    /// <summary>A conversation's title: the question collapsed to one line and shortened.</summary>
    internal static string TitleOf(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return NewConversationTitle;
        }

        string line = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= TitleLength)
        {
            return line;
        }

        int cut = TitleLength - 1;
        if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return line[..cut].TrimEnd() + "\u2026";
    }

    private async Task RunTurnAsync(Conversation conversation, Turn turn)
    {
        try
        {
            TurnEnd end;
            try
            {
                AnswerResult result = await StreamAsync(conversation, turn);
                BeforeCommit?.Invoke(conversation.Id);
                end = Completed(turn, result);
            }
            catch (OperationCanceledException) when (turn.Cancellation.IsCancellationRequested)
            {
                _logger.LogInformation("Turn stopped on {Model} at {Effort}.", turn.Settings.ModelId, turn.Settings.Effort);
                end = EndedEarly(turn, TurnOutcome.Stopped, StoppedNotice, failureKind: null);
            }
            catch (AnswerFailedException ex)
            {
                // The answer service has already logged it, with the exception that caused it.
                end = EndedEarly(turn, TurnOutcome.Failed, ex.Message + NotAddedSuffix, ex.Kind);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A turn failed unexpectedly on {Model} at {Effort}.", turn.Settings.ModelId, turn.Settings.Effort);
                end = EndedEarly(turn, TurnOutcome.Failed, UnexpectedFailure + NotAddedSuffix, AnswerFailureKind.Unexpected);
            }

            Publish(conversation, turn, end);
        }
        catch (Exception ex)
        {
            // Recording the outcome itself failed. Release the conversation, so it is not stuck in flight forever.
            _logger.LogCritical(ex, "A turn's outcome could not be recorded; its conversation was released and the question returned.");
            Release(conversation, turn);
        }
        finally
        {
            turn.Cache.End();

            // The turn left the conversation under its lock first, so Stop can no longer reach this source; a
            // Stop that read it just before is handled there.
            turn.Cancellation.Dispose();
        }
    }

    /// <summary>
    /// Streams the answer into the turn, and returns the completed response. From the final event on, the turn
    /// no longer watches its token: the answer is paid for, so it is kept.
    /// </summary>
    private async Task<AnswerResult> StreamAsync(Conversation conversation, Turn turn)
    {
        CancellationToken token = turn.Cancellation.Token;
        IAsyncEnumerator<AnswerEvent> events = _answers.StreamAsync(turn.Settings, turn.Request, token).GetAsyncEnumerator(token);
        try
        {
            while (await events.MoveNextAsync())
            {
                if (events.Current is AnswerEvent.Completed completed)
                {
                    return completed.Result;
                }

                OnEvent(conversation, turn, events.Current);
            }
        }
        finally
        {
            try
            {
                await events.DisposeAsync();
            }
            catch (Exception ex)
            {
                // The outcome is already decided; closing the stream must not replace it.
                _logger.LogWarning(ex, "Closing an answer stream failed on {Model} at {Effort}.", turn.Settings.ModelId, turn.Settings.Effort);
            }
        }

        _logger.LogError("The answer stream on {Model} at {Effort} ended without a final message.", turn.Settings.ModelId, turn.Settings.Effort);
        throw new AnswerFailedException(
            AnswerFailureKind.Unexpected, "The answer ended without a final message. Ask again; if it persists, see the log.", null);
    }

    private void OnEvent(Conversation conversation, Turn turn, AnswerEvent answerEvent)
    {
        // Recorded when the server reports it, so a stopped or failed turn that used the cache still counts.
        if (answerEvent is AnswerEvent.UsageStarted started)
        {
            turn.Cache.Started(started.Usage);
        }

        lock (conversation.Gate)
        {
            turn.Apply(answerEvent);
            conversation.Touch();
        }

        Raise(conversation.Id);
    }

    /// <summary>What a completed response adds: the quote report, cache check and warnings, and whether to commit it.</summary>
    /// <remarks>The stream has ended, so nothing writes to the turn any more, and reading it outside the lock is safe.</remarks>
    private TurnEnd Completed(Turn turn, AnswerResult result)
    {
        QuoteReport quotes = _quotes.BuildReport(result, turn.Corpus);
        CacheReport cache = _baselines.Record(
            turn.Cache.Key, result.Usage, turn.Cache.PredictedCold, _options.Prices[turn.Settings.ModelId]);
        List<ContentBlockParam> replay = Replayable(turn, result);
        bool commit = replay.Count > 0;

        List<TurnWarning> warnings = [];
        if (result.StopWarning is { } stop)
        {
            warnings.Add(new TurnWarning(ToWarningKind(stop.Kind), stop.Message));
        }

        if (cache.Warning is { } miss)
        {
            warnings.Add(new TurnWarning(TurnWarningKind.CacheMiss, miss));
        }

        if (result.CacheWarning is { } notCached)
        {
            warnings.Add(new TurnWarning(TurnWarningKind.NotCached, notCached));
        }

        if (ContextWarning(result.Usage.ContextTokens) is { } context)
        {
            warnings.Add(new TurnWarning(TurnWarningKind.Context, context));
        }

        if (cache.Status == CacheStatus.Miss)
        {
            _logger.LogWarning("{Warning}", cache.Warning);
        }

        AnswerRecord record = turn.ToRecord(commit ? TurnOutcome.Completed : TurnOutcome.NotReplayable) with
        {
            Result = result,
            Quotes = quotes,
            Cache = cache,
            Warnings = warnings.AsReadOnly(),
            Notice = commit ? null : NotReplayableNotice,
            Cost = result.Cost,
        };

        return new TurnEnd(record, commit ? replay : null, new ConversationCost(result.Cost, 0, 0), result.Usage.ContextTokens);
    }

    /// <summary>The reply as it can be sent back, or nothing when it cannot be.</summary>
    private List<ContentBlockParam> Replayable(Turn turn, AnswerResult result)
    {
        try
        {
            return ReplayConverter.ToParams(result.Message.Content);
        }
        catch (NotSupportedException ex)
        {
            // The answer service already refuses such a response, so this should not happen; if it does, the
            // answer stays visible and the conversation stays valid.
            _logger.LogError(ex, "A completed answer on {Model} at {Effort} cannot be replayed.", turn.Settings.ModelId, turn.Settings.Effort);
            return [];
        }
    }

    /// <summary>What a stopped or failed turn adds: its partial answer, and only what is known of its cost.</summary>
    private TurnEnd EndedEarly(Turn turn, TurnOutcome outcome, string notice, AnswerFailureKind? failureKind)
    {
        TurnCost? cost;
        string note;
        ConversationCost added;
        string cause = outcome == TurnOutcome.Stopped ? "Stop" : "the failure";

        if (failureKind == AnswerFailureKind.Credentials)
        {
            // The key could not be loaded, so no request was made.
            (cost, note, added) = (TurnCost.Zero, NothingSentNote, ConversationCost.Zero);
        }
        else if (turn.InputCost is { } input)
        {
            (cost, note, added) = (
                input,
                FormattableString.Invariant($"Input billed about ${input.Total:0.00}; output before {cause} not reported."),
                new ConversationCost(input, 0, 1));
        }
        else
        {
            // A cold prefill of 200K tokens takes seconds, so the write may already have been billed.
            CostEstimate estimate = _costs.Estimate(turn.Settings, turn.Corpus);
            decimal upTo = turn.Cache.PredictedCold ? estimate.ColdInput : estimate.WarmInput;
            (cost, note, added) = (
                null,
                FormattableString.Invariant($"Cost unknown: up to about ${upTo:0.00} if the request had started."),
                new ConversationCost(TurnCost.Zero, 1, 0));
        }

        AnswerRecord record = turn.ToRecord(outcome) with
        {
            Notice = notice,
            FailureKind = failureKind,
            Cost = cost,
            CostNote = note,
        };

        return new TurnEnd(record, null, added, null);
    }

    private void Publish(Conversation conversation, Turn turn, TurnEnd end)
    {
        lock (conversation.Gate)
        {
            if (end.Replay is { } replay)
            {
                conversation.Messages = [.. conversation.Messages, turn.UserMessage, MessageRequestBuilder.AssistantTurn(replay)];
                conversation.Corpus ??= turn.Corpus;
            }
            else
            {
                conversation.ReturnedQuestion = turn.Question;
            }

            conversation.Answers = [.. conversation.Answers, end.Record];
            conversation.Cost = conversation.Cost.Add(end.Cost);
            conversation.LastRequestTokens = end.RequestTokens ?? conversation.LastRequestTokens;
            conversation.LastOutcome = end.Record.Outcome;

            // Removed under the lock before the token source is disposed, so Stop can no longer reach it.
            conversation.InFlight = null;
            conversation.Touch();
        }

        Raise(conversation.Id);
    }

    private void Release(Conversation conversation, Turn turn)
    {
        lock (conversation.Gate)
        {
            if (conversation.InFlight != turn)
            {
                return;
            }

            conversation.InFlight = null;
            conversation.ReturnedQuestion = turn.Question;
            conversation.LastOutcome = TurnOutcome.Failed;
            conversation.Touch();
        }

        Raise(conversation.Id);
    }

    private string? ContextWarning(long requestTokens)
    {
        // Not cumulative: every turn resends the whole conversation, so it is this request that approaches the window.
        long limit = (long)(_options.ContextWindow * _options.ContextWarnFraction);
        if (requestTokens < limit)
        {
            return null;
        }

        int percent = (int)Math.Round(_options.ContextWarnFraction * 100);
        return FormattableString.Invariant(
                $"CONTEXT: this request was {requestTokens:N0} tokens, past {percent}% of the {_options.ContextWindow:N0}-token window.")
            + " The whole conversation is resent every turn, so it will keep growing until a request is rejected. Start a "
            + "new conversation to clear it; the corpus stays cached.";
    }

    private static TurnWarningKind ToWarningKind(StopWarningKind kind) => kind switch
    {
        StopWarningKind.Truncated => TurnWarningKind.Truncated,
        StopWarningKind.Refused => TurnWarningKind.Refused,
        _ => TurnWarningKind.Incomplete,
    };

    private void OnCorpusChanged()
    {
        string? fingerprint = (_corpus.State as CorpusState.Ready)?.Corpus.Fingerprint;
        string? replaced;
        lock (_gate)
        {
            if (fingerprint is null || fingerprint == _currentFingerprint)
            {
                return;
            }

            replaced = _currentFingerprint;
            _currentFingerprint = fingerprint;
        }

        // The old prefix is no longer what new conversations send, so its baselines would report misses.
        if (replaced is not null)
        {
            _baselines.DropFingerprint(replaced);
            _logger.LogInformation("The corpus changed from {Old} to {New}; the old corpus's cache baselines were dropped.", replaced, fingerprint);
        }
    }

    private Conversation? Find(Guid id)
    {
        lock (_gate)
        {
            return _conversations.GetValueOrDefault(id);
        }
    }

    private Conversation[] All()
    {
        lock (_gate)
        {
            return [.. _order];
        }
    }

    // Outside every lock, and one handler at a time, so a slow, disposed or throwing subscriber cannot hold up a
    // turn or starve the others.
    private void Raise(Guid id)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (Action<Guid> handler in handlers.GetInvocationList().Cast<Action<Guid>>())
        {
            try
            {
                handler(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A conversation subscriber threw; the turn and the other subscribers were unaffected.");
            }
        }
    }

    /// <summary>What a finished turn changes in its conversation.</summary>
    /// <param name="Record">The answer record to add.</param>
    /// <param name="Replay">The reply to commit, or <see langword="null"/> when nothing is committed.</param>
    /// <param name="Cost">What to add to the conversation's cost.</param>
    /// <param name="RequestTokens">The completed request's size in the context window, when it completed.</param>
    private sealed record TurnEnd(AnswerRecord Record, List<ContentBlockParam>? Replay, ConversationCost Cost, long? RequestTokens);

    /// <summary>One conversation's live state. Every member is read and written under <see cref="Gate"/>.</summary>
    private sealed class Conversation(Guid id, DateTimeOffset createdAt, AnswerSettings settings)
    {
        private ConversationSnapshot? _snapshot;

        public Lock Gate { get; } = new();

        public Guid Id { get; } = id;

        public AnswerSettings Settings { get; set; } = settings;

        public CorpusSnapshot? Corpus { get; set; }

        public IReadOnlyList<MessageParam> Messages { get; set; } = [];

        public IReadOnlyList<AnswerRecord> Answers { get; set; } = [];

        public Turn? InFlight { get; set; }

        public TurnOutcome? LastOutcome { get; set; }

        public string? ReturnedQuestion { get; set; }

        public ConversationCost Cost { get; set; } = ConversationCost.Zero;

        public long? LastRequestTokens { get; set; }

        /// <summary>Locked while a turn is in flight, and once a turn is committed: there is then something to protect.</summary>
        public bool SettingsLocked => InFlight is not null || Messages.Count > 0;

        /// <summary>Marks the state changed, so the next snapshot is built afresh.</summary>
        public void Touch() => _snapshot = null;

        /// <summary>The current snapshot, built once per change rather than once per read.</summary>
        public ConversationSnapshot Snapshot() => _snapshot ??= new ConversationSnapshot
        {
            Id = Id,
            Title = TitleOf(Answers.FirstOrDefault(answer => answer.IsCommitted)?.Question
                ?? InFlight?.Question
                ?? Answers.LastOrDefault()?.Question),
            CreatedAt = createdAt,
            Settings = Settings,
            SettingsLocked = SettingsLocked,
            Corpus = Corpus,
            Messages = Messages,
            Answers = Answers,
            InProgress = InFlight?.ToInProgress(),
            LastOutcome = LastOutcome,
            ReturnedQuestion = ReturnedQuestion,
            Cost = Cost,
            LastRequestTokens = LastRequestTokens,
        };
    }

    /// <summary>
    /// One question in flight. Its streamed state is written under its conversation's lock while the stream
    /// runs, and only read once the stream has ended.
    /// </summary>
    private sealed class Turn
    {
        private readonly List<SegmentBuilder> _segments = [];
        private readonly Dictionary<int, SegmentBuilder> _segmentsByBlock = [];
        private readonly StringBuilder _thinking = new();
        private bool _hasRedactedThinking;

        public required string Question { get; init; }

        public required AnswerSettings Settings { get; init; }

        public required CorpusSnapshot Corpus { get; init; }

        /// <summary>The new user message: committed, with the reply, only if the answer completes and can be replayed.</summary>
        public required MessageParam UserMessage { get; init; }

        /// <summary>The committed messages plus <see cref="UserMessage"/>.</summary>
        public required IReadOnlyList<MessageParam> Request { get; init; }

        public required DateTimeOffset AskedAt { get; init; }

        public required CacheWarmthTracker.CacheRequest Cache { get; init; }

        /// <summary>Cancelled only by Stop and by app shutdown.</summary>
        public required CancellationTokenSource Cancellation { get; init; }

        public bool StopRequested { get; set; }

        public TokenUsage? Usage { get; private set; }

        public TurnCost? InputCost { get; private set; }

        public void Apply(AnswerEvent answerEvent)
        {
            switch (answerEvent)
            {
                case AnswerEvent.UsageStarted started:
                    Usage = started.Usage;
                    InputCost = started.Cost;
                    break;
                case AnswerEvent.ThinkingDelta thinking:
                    _thinking.Append(thinking.Text);
                    break;
                case AnswerEvent.ThinkingRedacted:
                    _hasRedactedThinking = true;
                    break;
                case AnswerEvent.TextDelta text:
                    Segment(text.BlockIndex).Text.Append(text.Text);
                    break;
                case AnswerEvent.TextBlockCompleted block when block.SourceNumbers.Count > 0 || _segmentsByBlock.ContainsKey(block.BlockIndex):
                    // The markers go after the block: a citation can arrive before the text it cites.
                    Segment(block.BlockIndex).SourceNumbers = block.SourceNumbers;
                    break;
            }
        }

        public InProgressAnswer ToInProgress() => new()
        {
            Question = Question,
            Settings = Settings,
            AskedAt = AskedAt,
            Segments = Segments(),
            Thinking = _thinking.ToString(),
            HasRedactedThinking = _hasRedactedThinking,
            Usage = Usage,
            IsStopping = StopRequested,
        };

        public AnswerRecord ToRecord(TurnOutcome outcome) => new()
        {
            Question = Question,
            Settings = Settings,
            Corpus = Corpus,
            AskedAt = AskedAt,
            Outcome = outcome,
            Segments = Segments(),
            Thinking = _thinking.ToString(),
            HasRedactedThinking = _hasRedactedThinking,
        };

        private IReadOnlyList<AnswerSegment> Segments() =>
            [.. _segments.Select(segment => new AnswerSegment(segment.Text.ToString(), segment.SourceNumbers))];

        private SegmentBuilder Segment(int blockIndex)
        {
            if (!_segmentsByBlock.TryGetValue(blockIndex, out SegmentBuilder? segment))
            {
                segment = new SegmentBuilder();
                _segmentsByBlock[blockIndex] = segment;
                _segments.Add(segment);
            }

            return segment;
        }
    }

    private sealed class SegmentBuilder
    {
        public StringBuilder Text { get; } = new();

        public IReadOnlyList<int> SourceNumbers { get; set; } = [];
    }
}
