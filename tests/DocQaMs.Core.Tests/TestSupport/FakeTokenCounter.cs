using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>One call to <see cref="FakeTokenCounter"/>.</summary>
/// <param name="Number">The call's number, counting from 1.</param>
/// <param name="ModelId">The model counted on.</param>
/// <param name="Documents">The documents counted.</param>
internal sealed record CountCall(int Number, string ModelId, IReadOnlyList<RequestDocument> Documents)
{
    /// <summary>
    /// Whether this is one of the whole-corpus counts, which <see cref="CorpusLoader"/> makes first, once
    /// per selectable model, before any per-document count.
    /// </summary>
    public bool IsCorpusCount => Number <= CacheAffecting.SelectableModels.Count;
}

/// <summary>A token counter that answers from a function and records every call.</summary>
internal sealed class FakeTokenCounter : ITokenCounter
{
    private readonly Func<CountCall, long> _count;
    private readonly List<CountCall> _calls = [];

    /// <summary>Counts the corpus as <paramref name="corpusTokens"/> on every model, and a document as its length.</summary>
    public FakeTokenCounter(long corpusTokens = 1_000)
        : this(call => call.IsCorpusCount ? corpusTokens : call.Documents[0].Text.Length)
    {
    }

    public FakeTokenCounter(Func<CountCall, long> count) => _count = count;

    public IReadOnlyList<CountCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<long> CountAsync(string modelId, IReadOnlyList<RequestDocument> documents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CountCall call;
        lock (_calls)
        {
            call = new CountCall(_calls.Count + 1, modelId, [.. documents]);
            _calls.Add(call);
        }

        return Task.FromResult(_count(call));
    }
}

/// <summary>Builds corpora for tests through the real <see cref="CorpusLoader"/>.</summary>
internal static class Corpora
{
    public const int Ceiling = 400_000;

    public static Task<DocQaMs.Core.Corpus.Corpus> OfAsync(params ExtractedDocument[] documents) => OfAsync(1_000, documents);

    public static Task<DocQaMs.Core.Corpus.Corpus> OfAsync(long tokens, params ExtractedDocument[] documents) =>
        new CorpusLoader(new FakeTokenCounter(tokens), NullLogger<CorpusLoader>.Instance)
            .AssembleAsync(documents, Ceiling, TestContext.Current.CancellationToken);
}
