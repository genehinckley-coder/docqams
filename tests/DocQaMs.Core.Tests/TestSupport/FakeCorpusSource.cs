using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>A corpus source whose state a test sets directly.</summary>
internal sealed class FakeCorpusSource(CorpusState state) : ICorpusSource
{
    private CorpusState _state = state;

    public CorpusState State => Volatile.Read(ref _state);

    public event Action? StateChanged;

    /// <summary>How many times <see cref="ReloadAsync"/> was called.</summary>
    public int Reloads { get; private set; }

    /// <summary>Counts the call; a test publishes the outcome itself.</summary>
    public Task ReloadAsync()
    {
        Reloads++;
        return Task.CompletedTask;
    }

    public static CorpusState.Ready Ready(CorpusSnapshot corpus) =>
        new(corpus, new FingerprintCheck(FingerprintStatus.FirstRun, null, []), "", DateTimeOffset.UnixEpoch);

    public void Publish(CorpusState state)
    {
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke();
    }
}

/// <summary>Corpus snapshots over the item 1 spike fixtures, with a chosen fingerprint.</summary>
internal static class TestCorpus
{
    /// <summary>The token count on every model: about the size of the real corpus.</summary>
    public const long Tokens = 200_000;

    /// <summary>Fixture A then fixture B, so a citation of document 0 is fixture A.</summary>
    public static CorpusSnapshot Create(string fingerprint = "fingerprint-1") => new(
        [
            new CorpusDocument(SpikeFixtures.FileA, SpikeFixtures.Documents[0], null),
            new CorpusDocument(SpikeFixtures.FileB, SpikeFixtures.Documents[1], null),
        ],
        fingerprint,
        CacheAffecting.SelectableModels.ToDictionary(model => model.Id, _ => Tokens));
}
