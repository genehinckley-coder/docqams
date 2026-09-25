using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Conversations;

/// <summary>
/// Identifies one 1-hour corpus cache entry: a corpus, a model and an effort level.
/// </summary>
/// <remarks>
/// Caches are scoped to the model, and a change of effort invalidates the messages cache, which holds the
/// corpus. So each combination of corpus, model and level has its own entry, and its own cold write (plan
/// item 1 confirmed that entries for different combinations coexist).
/// </remarks>
/// <param name="Fingerprint">The corpus fingerprint.</param>
/// <param name="ModelId">The model id.</param>
/// <param name="Effort">The effort level.</param>
public readonly record struct CorpusCacheKey(string Fingerprint, string ModelId, EffortLevel Effort)
{
    /// <summary>The entry a request for <paramref name="corpus"/> at <paramref name="settings"/> reads or writes.</summary>
    /// <param name="corpus">The corpus snapshot the request is built from.</param>
    /// <param name="settings">The model and level.</param>
    /// <returns>The key.</returns>
    public static CorpusCacheKey For(CorpusSnapshot corpus, AnswerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(settings);
        return new CorpusCacheKey(corpus.Fingerprint, settings.ModelId, settings.Effort);
    }
}
