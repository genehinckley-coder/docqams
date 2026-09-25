using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// Assembles extracted documents into a <see cref="Corpus"/>: ordered, counted, size-guarded and
/// fingerprinted.
/// </summary>
/// <remarks>
/// Ported from the NotRag prototype's <c>corpus.py</c>. The worst failure in this kind of app is
/// answering confidently from part of the corpus, so assembly never truncates: a corpus over the ceiling
/// is refused with the options listed.
/// </remarks>
public sealed class CorpusLoader
{
    private readonly ITokenCounter _tokenCounter;
    private readonly ILogger<CorpusLoader> _logger;

    /// <summary>Creates the loader.</summary>
    /// <param name="tokenCounter">Counts the assembled request's input tokens.</param>
    /// <param name="logger">Logs a failed per-document count, which does not stop the load.</param>
    public CorpusLoader(ITokenCounter tokenCounter, ILogger<CorpusLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentNullException.ThrowIfNull(logger);
        _tokenCounter = tokenCounter;
        _logger = logger;
    }

    /// <summary>
    /// Orders, counts, size-guards and fingerprints the documents.
    /// </summary>
    /// <remarks>
    /// The corpus is counted once on each selectable model, and the guard applies to the largest count.
    /// Only then is each document counted on its own on <see cref="CacheAffecting.DefaultModelId"/>, for
    /// display; a failure there is logged, and those documents show no count.
    /// </remarks>
    /// <param name="documents">The extracted documents, in any order.</param>
    /// <param name="ceiling">The largest acceptable token count, inclusive.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The corpus.</returns>
    /// <exception cref="ArgumentException"><paramref name="documents"/> is empty or names a file twice.</exception>
    /// <exception cref="CorpusTooLargeException">
    /// A model's count exceeds <paramref name="ceiling"/>. The corpus is never truncated to fit.
    /// </exception>
    public async Task<Corpus> AssembleAsync(
        IReadOnlyList<ExtractedDocument> documents, int ceiling, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);
        if (documents.Count == 0)
        {
            throw new ArgumentException("Cannot assemble an empty corpus.", nameof(documents));
        }

        // Ordinal, as Python sorts: a culture-aware sort could reorder the documents, and with them every
        // citation's document_index.
        List<ExtractedDocument> ordered = [.. documents.OrderBy(document => document.FileName, StringComparer.Ordinal)];
        List<string> duplicates = ordered
            .GroupBy(document => document.FileName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            // Sorting cannot order two documents with one name, so the send order, and with it the
            // fingerprint, would depend on the caller's order.
            throw new ArgumentException(
                $"The corpus names a file more than once: {string.Join(", ", duplicates)}.", nameof(documents));
        }

        List<RequestDocument> requests =
        [
            .. ordered.Select(document =>
                new RequestDocument(document.Label, CacheAffecting.RenderDocumentContext(document.FileName), document.Text)),
        ];

        // Every selectable model, since their tokenizers can differ: Opus 5.5 counts a couple of tokens
        // more than Opus 5 and Sonnet 5. The ceiling applies to the largest count.
        Dictionary<string, long> tokenCounts = new(StringComparer.Ordinal);
        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            tokenCounts[model.Id] = await _tokenCounter.CountAsync(model.Id, requests, cancellationToken);
        }

        if (tokenCounts.Values.Max() > ceiling)
        {
            throw new CorpusTooLargeException(tokenCounts, ceiling);
        }

        long?[] approximateTokens = await CountEachDocumentAsync(requests, cancellationToken);
        CorpusDocument[] corpusDocuments =
        [
            .. ordered.Select((document, i) => new CorpusDocument(document.FileName, requests[i], approximateTokens[i])),
        ];

        return new Corpus(Array.AsReadOnly(corpusDocuments), CorpusFingerprint.Compute(requests), tokenCounts);
    }

    // Display-only, so a failure (a rate limit, say) must not stop the corpus from loading. The first
    // failure ends the counting, since the calls after it would most likely fail the same way.
    private async Task<long?[]> CountEachDocumentAsync(List<RequestDocument> requests, CancellationToken cancellationToken)
    {
        var counts = new long?[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            try
            {
                counts[i] = await _tokenCounter.CountAsync(CacheAffecting.DefaultModelId, [requests[i]], cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Could not count the tokens of \"{Document}\" on its own. It and the {Remaining} document(s) after it show no approximate token count.",
                    requests[i].Title,
                    requests.Count - i - 1);
                break;
            }
        }

        return counts;
    }
}
