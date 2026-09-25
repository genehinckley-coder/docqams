namespace DocQaMs.Core.Answering;

/// <summary>What kind of failure ended an answer.</summary>
public enum AnswerFailureKind
{
    /// <summary>The API key file is missing or malformed. No request was sent.</summary>
    Credentials,

    /// <summary>The API rejected the key (401), or the key may not make this request (403).</summary>
    Authentication,

    /// <summary>Rate limited or out of quota (429).</summary>
    RateLimited,

    /// <summary>The account has no credit left.</summary>
    CreditExhausted,

    /// <summary>Any other error the API reported, before or during the stream.</summary>
    ApiError,

    /// <summary>The connection to the API failed.</summary>
    Connection,

    /// <summary>The stream ended before the provider signalled the end of the message: the answer is incomplete.</summary>
    StreamEndedEarly,

    /// <summary>No complete answer within the level's timeout.</summary>
    TimedOut,

    /// <summary>Anything else, such as a response this app cannot read.</summary>
    Unexpected,
}

/// <summary>
/// An answer failed. <see cref="Exception.Message"/> is a short message for the user; the exception that
/// caused it is kept as <see cref="Exception.InnerException"/>, for the log.
/// </summary>
/// <remarks>The message never contains key text or the request body.</remarks>
public sealed class AnswerFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">What kind of failure it was.</param>
    /// <param name="message">A short message for the user.</param>
    /// <param name="innerException">The exception that caused it.</param>
    public AnswerFailedException(AnswerFailureKind kind, string message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>What kind of failure it was.</summary>
    public AnswerFailureKind Kind { get; }
}
