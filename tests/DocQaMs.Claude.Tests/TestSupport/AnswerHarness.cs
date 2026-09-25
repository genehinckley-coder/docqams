using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Claude.Tests.TestSupport;

/// <summary>
/// An <see cref="AnswerService"/> over the real Claude pipeline, wired to a test's innermost HTTP handler,
/// with the real per-level <c>max_tokens</c> and both models' real prices.
/// </summary>
/// <remarks>
/// As in production, each level's chat client has the level's <see cref="DocQaOptions.RequestTimeouts"/> as
/// its whole-request timeout, unless <c>sdkTimeout</c> overrides it.
/// </remarks>
internal sealed class AnswerHarness : IDisposable
{
    public AnswerHarness(
        DelegatingHandler innermost,
        Action<DocQaOptions>? configure = null,
        TimeSpan? sdkTimeout = null,
        TimeProvider? timeProvider = null)
    {
        Options = TestSettings.ValidOptions();
        Options.MaxTokens[EffortLevel.XHigh] = 64_000;
        Options.MaxTokens[EffortLevel.Max] = 128_000;
        Options.Prices[ModelIds.ClaudeSonnet5] = new ModelPrices
        {
            InputPerMTok = 2m,
            CacheWrite5mPerMTok = 2.5m,
            CacheWrite1hPerMTok = 4m,
            CacheReadPerMTok = 0.2m,
            OutputPerMTok = 10m,
        };
        configure?.Invoke(Options);

        Provider = ClaudeChatClientProvider.Create(
            ClaudeTestClient.FakeKey,
            sdkTimeout is { } timeout ? ClaudeTestClient.Timeouts(timeout) : Options.RequestTimeouts,
            [innermost],
            ClaudeLog);
        Service = new AnswerService(
            () => Provider, Microsoft.Extensions.Options.Options.Create(Options), timeProvider ?? TimeProvider.System, Logger);
    }

    /// <summary>A first question against the item 1 spike fixtures.</summary>
    public static IReadOnlyList<ChatMessage> FirstQuestion { get; } = [ClaudeTestClient.FirstQuestion()];

    public DocQaOptions Options { get; }

    public ClaudeChatClientProvider Provider { get; }

    public AnswerService Service { get; }

    /// <summary>The answer service's log.</summary>
    public ListLogger<AnswerService> Logger { get; } = new();

    /// <summary>The Claude pipeline's log.</summary>
    public ListLoggerFactory ClaudeLog { get; } = new();

    /// <summary>
    /// Streams an answer into <paramref name="received"/>, so a test can inspect the events that arrived
    /// before an exception.
    /// </summary>
    public async Task CollectAsync(
        List<AnswerEvent> received,
        AnswerSettings? settings = null,
        IReadOnlyList<ChatMessage>? messages = null,
        Action<AnswerEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        await foreach (AnswerEvent answerEvent in Service.StreamAsync(
            settings ?? AnswerSettings.Default, messages ?? FirstQuestion, cancellationToken))
        {
            received.Add(answerEvent);
            onEvent?.Invoke(answerEvent);
        }
    }

    /// <summary>Streams an answer that must complete, and returns its result.</summary>
    public async Task<AnswerResult> CompleteAsync(
        AnswerSettings? settings = null, IReadOnlyList<ChatMessage>? messages = null, CancellationToken cancellationToken = default)
    {
        List<AnswerEvent> received = [];
        await CollectAsync(received, settings, messages, cancellationToken: cancellationToken);
        return Assert.IsType<AnswerEvent.Completed>(received[^1]).Result;
    }

    public void Dispose() => Provider.Dispose();
}
