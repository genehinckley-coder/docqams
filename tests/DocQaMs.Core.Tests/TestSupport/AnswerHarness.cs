using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// An <see cref="AnswerService"/> wired to a test's innermost HTTP handler, with the real per-level
/// <c>max_tokens</c> and both models' real prices.
/// </summary>
internal sealed class AnswerHarness : IDisposable
{
    public AnswerHarness(DelegatingHandler innermost, Action<DocQaOptions>? configure = null, TimeSpan? sdkTimeout = null)
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

        Provider = AnthropicClientProvider.Create(
            SinglePass.FakeKey, SinglePass.Timeouts(sdkTimeout ?? TimeSpan.FromMinutes(1)), [innermost]);
        Service = new AnswerService(() => Provider, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, Logger);
    }

    /// <summary>A first question against the item 1 spike fixtures.</summary>
    public static IReadOnlyList<MessageParam> FirstQuestion { get; } =
        [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?")];

    public DocQaOptions Options { get; }

    public AnthropicClientProvider Provider { get; }

    public AnswerService Service { get; }

    public ListLogger<AnswerService> Logger { get; } = new();

    /// <summary>
    /// Streams an answer into <paramref name="received"/>, so a test can inspect the events that arrived
    /// before an exception.
    /// </summary>
    public async Task CollectAsync(
        List<AnswerEvent> received,
        AnswerSettings? settings = null,
        IReadOnlyList<MessageParam>? messages = null,
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
    public async Task<AnswerResult> CompleteAsync(AnswerSettings? settings = null, CancellationToken cancellationToken = default)
    {
        List<AnswerEvent> received = [];
        await CollectAsync(received, settings, cancellationToken: cancellationToken);
        return Assert.IsType<AnswerEvent.Completed>(received[^1]).Result;
    }

    public void Dispose() => Provider.Dispose();
}
