using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// A <see cref="ConversationStore"/> over a scripted answer service (or a given one), a fake corpus source and a
/// fake clock, with the real quote checker, warmth tracker, baselines and estimator.
/// </summary>
internal sealed class StoreHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    public StoreHarness(CorpusState? corpusState = null, IAnswerService? answers = null)
    {
        Options = TestSettings.ValidOptions();
        Options.Prices[ModelIds.ClaudeSonnet5] = new ModelPrices
        {
            InputPerMTok = 2m,
            CacheWrite5mPerMTok = 2.5m,
            CacheWrite1hPerMTok = 4m,
            CacheReadPerMTok = 0.2m,
            OutputPerMTok = 10m,
        };
        IOptions<DocQaOptions> options = Microsoft.Extensions.Options.Options.Create(Options);

        Corpus = TestCorpus.Create();
        CorpusSource = new FakeCorpusSource(corpusState ?? FakeCorpusSource.Ready(Corpus));
        Warmth = new CacheWarmthTracker(Time);
        Baselines = new CacheBaselines(options);
        Costs = new CostEstimator(options);
        Store = new ConversationStore(
            answers ?? Answers, new QuoteChecker(options), CorpusSource, Warmth, Baselines, Costs, options, Time, Logger);
    }

    public DocQaOptions Options { get; }

    public FakeTimeProvider Time { get; } = new(Start);

    public ScriptedAnswerService Answers { get; } = new();

    public CorpusSnapshot Corpus { get; }

    public FakeCorpusSource CorpusSource { get; }

    public CacheWarmthTracker Warmth { get; }

    public CacheBaselines Baselines { get; }

    public CostEstimator Costs { get; }

    public ListLogger<ConversationStore> Logger { get; } = new();

    public ConversationStore Store { get; }

    /// <summary>Asks, and fails the test if the store refuses.</summary>
    public Task AskAsync(Guid id, string question)
    {
        AskResult result = Store.Ask(id, question);
        Assert.True(result.Accepted, result.Refusal);
        return result.Turn;
    }

    /// <summary>Asks, answers with <paramref name="result"/>, and waits for the turn to be published.</summary>
    public async Task<ConversationSnapshot> AnswerAsync(Guid id, string question, AnswerResult result)
    {
        Task turn = AskAsync(id, question);
        (await Answers.NextAsync()).Answer(result);
        await WaitAsync(turn);
        return Store.Get(id)!;
    }

    /// <summary>Waits for a snapshot that satisfies <paramref name="condition"/>. Fails the test after 10 seconds.</summary>
    public async Task<ConversationSnapshot> WaitForAsync(Guid id, Func<ConversationSnapshot, bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            ConversationSnapshot snapshot = Store.Get(id)!;
            if (condition(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(5, timeout.Token);
        }
    }

    public static Task WaitAsync(Task turn) => turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    public void Dispose() => Store.Dispose();
}

/// <summary>Completed responses, built by the real <see cref="AnswerService"/> from canned stream events.</summary>
internal static class Replies
{
    public static async Task<AnswerResult> OfAsync(IEnumerable<string> events, AnswerSettings? settings = null)
    {
        var stub = new StubHandler(_ => Sse.Response(events));
        using var harness = new AnswerHarness(stub);
        return await harness.CompleteAsync(settings, TestContext.Current.CancellationToken);
    }

    /// <summary>A one-block answer with the given usage.</summary>
    public static Task<AnswerResult> TextAsync(
        long write1h = 2_080, long write5m = 20, long read = 0, long input = 12, AnswerSettings? settings = null) =>
        OfAsync([StreamEvents.Start(input, write1h, write5m, read), .. StreamEvents.Text(0, "An answer."), .. StreamEvents.End()], settings);

    /// <summary>A refusal with no content at all: nothing that can be sent back.</summary>
    public static Task<AnswerResult> EmptyRefusalAsync() =>
        OfAsync([StreamEvents.Start(), .. StreamEvents.End("refusal")]);
}
