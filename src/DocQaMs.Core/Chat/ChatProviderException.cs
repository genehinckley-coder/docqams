namespace DocQaMs.Core.Chat;

/// <summary>
/// What went wrong in a failed call to the chat provider, in provider-neutral terms.
/// </summary>
public enum ChatProviderFailure
{
    /// <summary>The provider rejected the credentials (HTTP 401).</summary>
    Authentication,

    /// <summary>The credentials are not permitted to make the request (HTTP 403).</summary>
    Forbidden,

    /// <summary>Rate limited or out of quota (HTTP 429).</summary>
    RateLimited,

    /// <summary>The account is out of credit.</summary>
    CreditExhausted,

    /// <summary>The provider returned an error response. <see cref="ChatProviderException.StatusCode"/> is set.</summary>
    ApiError,

    /// <summary>The provider reported an error partway through a streamed response.</summary>
    StreamError,

    /// <summary>The response stream ended, or broke, before the response was complete.</summary>
    StreamEndedEarly,

    /// <summary>The connection to the provider failed.</summary>
    Connection,
}

/// <summary>
/// A failed call to the chat provider, translated from the provider SDK's own exception so the app can
/// explain it without knowing which provider answers.
/// </summary>
/// <remarks>
/// The provider's chat client and token counter throw this for failures they recognize; the original
/// exception is <see cref="Exception.InnerException"/>. Cancellation is never translated: it surfaces as
/// <see cref="OperationCanceledException"/>.
/// </remarks>
public sealed class ChatProviderException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What went wrong.</param>
    /// <param name="message">A technical description, for the log.</param>
    /// <param name="innerException">The provider SDK's exception.</param>
    /// <param name="statusCode">The HTTP status code, when there was a response.</param>
    /// <param name="errorType">The provider's error type (for example <c>overloaded_error</c>), when it gave one.</param>
    public ChatProviderException(
        ChatProviderFailure failure,
        string message,
        Exception? innerException = null,
        int? statusCode = null,
        string? errorType = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        ErrorType = errorType;
    }

    /// <summary>What went wrong.</summary>
    public ChatProviderFailure Failure { get; }

    /// <summary>The HTTP status code, when there was a response.</summary>
    public int? StatusCode { get; }

    /// <summary>The provider's error type, when it gave one.</summary>
    public string? ErrorType { get; }
}
