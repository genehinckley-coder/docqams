using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;

namespace DocQaMs.Claude;

/// <summary>
/// An <see cref="ITokenCounter"/> backed by the Messages API's free <c>count_tokens</c> call.
/// </summary>
/// <remarks>
/// It counts the request exactly as a first question sends it: the message built by
/// <see cref="MessageRequestBuilder.FirstTurn"/>, rendered by the same mapper as the chat client, with
/// <see cref="CacheAffecting.SystemPrompt"/>, which renders before the documents and so belongs in the
/// count.
/// </remarks>
public sealed class ClaudeTokenCounter : ITokenCounter
{
    /// <summary>
    /// Stands in for the question: a request needs one to be valid, and a single character adds a token
    /// or two to the count.
    /// </summary>
    public const string PlaceholderQuestion = "?";

    private readonly Func<ClaudeChatClientProvider> _clientProvider;

    /// <summary>Creates the counter.</summary>
    /// <param name="clientProvider">
    /// Supplies the client when a count is made, not before. The client is built on first use, so a
    /// missing or malformed key file surfaces as a failed count, which the UI can show and retry, instead
    /// of stopping the app from starting.
    /// </param>
    public ClaudeTokenCounter(Func<ClaudeChatClientProvider> clientProvider)
    {
        ArgumentNullException.ThrowIfNull(clientProvider);
        _clientProvider = clientProvider;
    }

    /// <inheritdoc/>
    /// <exception cref="FileNotFoundException">The API key file does not exist.</exception>
    /// <exception cref="InvalidDataException">The API key file is malformed.</exception>
    /// <exception cref="ChatProviderException">The API, or the connection to it, failed.</exception>
    /// <exception cref="OperationCanceledException">The count was cancelled or timed out.</exception>
    public async Task<long> CountAsync(string modelId, IReadOnlyList<RequestDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);

        var request = new MessageCountTokensParams
        {
            Model = modelId,
            System = CacheAffecting.SystemPrompt,
            Messages = [ClaudeMessageMapper.ToMessageParam(MessageRequestBuilder.FirstTurn(documents, PlaceholderQuestion))],
        };

        ClaudeChatClientProvider provider = _clientProvider();
        try
        {
            MessageTokensCount count = await provider.AnthropicClient.Messages.CountTokens(request, cancellationToken)
                .ConfigureAwait(false);
            return count.InputTokens;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ClaudeFailures.Translate(ex) is { } failure)
        {
            throw failure;
        }
    }
}
