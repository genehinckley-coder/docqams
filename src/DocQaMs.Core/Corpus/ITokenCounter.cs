using DocQaMs.Core.Answering;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// Counts the input tokens of a request, for the corpus size guard.
/// </summary>
/// <remarks>
/// Injectable, so the guard is testable at its exact boundary without the API.
/// </remarks>
public interface ITokenCounter
{
    /// <summary>
    /// Counts the input tokens of a first question on <paramref name="modelId"/> that carries
    /// <paramref name="documents"/>: the system prompt, the document blocks and a placeholder question.
    /// </summary>
    /// <param name="modelId">The model whose tokenizer counts.</param>
    /// <param name="documents">The documents, in send order.</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    /// <returns>The input token count.</returns>
    Task<long> CountAsync(string modelId, IReadOnlyList<RequestDocument> documents, CancellationToken cancellationToken);
}
