using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// An <see cref="ITokenCounter"/> backed by the Messages API's free <c>count_tokens</c> call.
/// </summary>
/// <remarks>
/// It counts the request exactly as a first question sends it, built by
/// <see cref="MessageRequestBuilder.FirstTurn"/> with <see cref="CacheAffecting.SystemPrompt"/>, which
/// renders before the documents and so belongs in the count.
/// </remarks>
public sealed class ApiTokenCounter : ITokenCounter
{
    /// <summary>
    /// Stands in for the question: a request needs one to be valid, and a single character adds a token
    /// or two to the count.
    /// </summary>
    public const string PlaceholderQuestion = "?";

    private readonly Func<IAnthropicClientProvider> _clientProvider;

    /// <summary>Creates the counter.</summary>
    /// <param name="clientProvider">
    /// Supplies the client when a count is made, not before. The client is built on first use, so a
    /// missing or malformed key file surfaces as a failed count, which the UI can show and retry, instead
    /// of stopping the app from starting.
    /// </param>
    public ApiTokenCounter(Func<IAnthropicClientProvider> clientProvider)
    {
        ArgumentNullException.ThrowIfNull(clientProvider);
        _clientProvider = clientProvider;
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(string modelId, IReadOnlyList<RequestDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);

        MessageTokensCount count = await _clientProvider().Client.Messages.CountTokens(
            new MessageCountTokensParams
            {
                Model = modelId,
                System = CacheAffecting.SystemPrompt,
                Messages = [MessageRequestBuilder.FirstTurn(documents, PlaceholderQuestion)],
            },
            cancellationToken);

        return count.InputTokens;
    }
}
