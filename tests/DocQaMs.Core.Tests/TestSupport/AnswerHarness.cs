using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// An <see cref="AnswerService"/> over a <see cref="FakeChatClient"/>, with the real per-level
/// <c>max_tokens</c> and both models' real prices.
/// </summary>
internal sealed class AnswerHarness : IDisposable
{
    public AnswerHarness(FakeChatClient client, Action<DocQaOptions>? configure = null, TimeProvider? time = null)
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

        Client = client;
        Provider = new FakeChatClientProvider(client);
        Service = new AnswerService(
            () => Provider, Microsoft.Extensions.Options.Options.Create(Options), time ?? TimeProvider.System, Logger);
    }

    /// <summary>An answer service whose every request is answered with <paramref name="updates"/>.</summary>
    public AnswerHarness(IEnumerable<ChatResponseUpdate> updates, Action<DocQaOptions>? configure = null, TimeProvider? time = null)
        : this(new FakeChatClient(updates), configure, time)
    {
    }

    /// <summary>A first question against the item 1 spike fixtures.</summary>
    public static IReadOnlyList<ChatMessage> FirstQuestion { get; } =
        [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?")];

    public DocQaOptions Options { get; }

    public FakeChatClient Client { get; }

    public FakeChatClientProvider Provider { get; }

    public AnswerService Service { get; }

    public ListLogger<AnswerService> Logger { get; } = new();

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

    public void Dispose() => Client.Dispose();
}
