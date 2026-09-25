using System.Collections.Frozen;
using DocQaMs.Core.Answering;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// An immutable snapshot of the assembled corpus: the documents in send order, fingerprinted and counted.
/// </summary>
/// <remarks>
/// A conversation keeps the snapshot its first request was built from, and quote-checks and highlights
/// against that snapshot, never against whichever corpus is current after a reload.
/// </remarks>
public sealed class Corpus
{
    internal Corpus(IReadOnlyList<CorpusDocument> documents, string fingerprint, IReadOnlyDictionary<string, long> tokenCounts)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentNullException.ThrowIfNull(tokenCounts);
        if (documents.Count == 0 || tokenCounts.Count == 0)
        {
            throw new ArgumentException("A corpus needs at least one document and one token count.");
        }

        Documents = documents;
        RequestDocuments = documents.Select(document => document.Request).ToList().AsReadOnly();
        Fingerprint = fingerprint;
        TokenCounts = tokenCounts.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The documents in send order. A citation's <c>document_index</c> indexes this list.</summary>
    public IReadOnlyList<CorpusDocument> Documents { get; }

    /// <summary>The documents as <see cref="MessageRequestBuilder"/> takes them, in the same order.</summary>
    public IReadOnlyList<RequestDocument> RequestDocuments { get; }

    /// <summary>
    /// SHA-256 of the document blocks' canonical serialization, in lowercase hex; see
    /// <see cref="CorpusFingerprint"/>. The model and effort level are not part of it.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// The input tokens of a first question on each selectable model, keyed by model id: the system prompt,
    /// every document block and a placeholder question.
    /// </summary>
    public IReadOnlyDictionary<string, long> TokenCounts { get; }

    /// <summary>The largest of <see cref="TokenCounts"/>: the figure the size guard applies to.</summary>
    public long LargestTokenCount => TokenCounts.Values.Max();
}
