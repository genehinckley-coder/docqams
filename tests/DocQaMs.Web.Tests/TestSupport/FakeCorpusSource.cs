using DocQaMs.Core.Corpus;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Web.Tests.TestSupport;

/// <summary>A corpus source whose state a test sets directly.</summary>
internal sealed class FakeCorpusSource(CorpusState state) : ICorpusSource
{
    private CorpusState _state = state;

    public CorpusState State => Volatile.Read(ref _state);

    public event Action? StateChanged;

    /// <summary>How many times <see cref="ReloadAsync"/> was called.</summary>
    public int Reloads { get; private set; }

    /// <summary>How many handlers are subscribed to <see cref="StateChanged"/>.</summary>
    public int SubscriberCount => StateChanged?.GetInvocationList().Length ?? 0;

    public static CorpusState.Ready Ready(CorpusSnapshot corpus) =>
        new(corpus, new FingerprintCheck(FingerprintStatus.FirstRun, null, []), "", DateTimeOffset.UnixEpoch);

    /// <summary>Counts the call; a test publishes the outcome itself.</summary>
    public Task ReloadAsync()
    {
        Reloads++;
        return Task.CompletedTask;
    }

    public void Publish(CorpusState state)
    {
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke();
    }
}
