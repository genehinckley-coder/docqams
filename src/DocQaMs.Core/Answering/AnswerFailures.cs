using System.Globalization;
using System.Text;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Maps what can go wrong during an answer to an <see cref="AnswerFailedException"/>, most specific first.
/// </summary>
/// <remarks>
/// The provider's chat client translates the failures it recognizes into a <see cref="ChatProviderException"/>,
/// whose <see cref="ChatProviderException.Failure"/> picks the message. Plan item 1 found failures that can
/// still arrive untranslated: a body that breaks after the response started (a raw
/// <see cref="IOException"/>), and the client's own timeout (a <see cref="TaskCanceledException"/> with the
/// caller's token not cancelled). Stop is not a failure and never reaches this mapping.
/// </remarks>
internal static class AnswerFailures
{
    /// <summary>Maps <paramref name="exception"/>.</summary>
    /// <param name="exception">What was thrown.</param>
    /// <param name="effort">The level the answer was requested at.</param>
    /// <param name="timedOutAfter">
    /// The timeout that ended the request, or <see langword="null"/> when no timeout did. Whatever the
    /// stream threw once a timeout fired, the timeout is the cause.
    /// </param>
    public static AnswerFailedException Map(Exception exception, EffortLevel effort, TimeSpan? timedOutAfter)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is AnswerFailedException failed)
        {
            return failed;
        }

        if (timedOutAfter is { } timeout)
        {
            return new AnswerFailedException(
                AnswerFailureKind.TimedOut,
                $"No complete answer within {Duration(timeout)}, the limit for {effort.ToDisplayName()} effort, " +
                "so the request was ended. Ask again, or ask a narrower question.",
                exception);
        }

        // Neither Stop nor a timeout asked for it, so calling it a timeout would mislead.
        if (exception is OperationCanceledException)
        {
            return new AnswerFailedException(
                AnswerFailureKind.Unexpected,
                "The request was cancelled, though neither Stop nor a timeout asked for it. Ask again; if it persists, see the log.",
                exception);
        }

        return exception switch
        {
            ChatProviderException { Failure: ChatProviderFailure.CreditExhausted } => new AnswerFailedException(
                AnswerFailureKind.CreditExhausted,
                "The API account is out of credit. Add credit in the Claude Console, then ask again.",
                exception),
            ChatProviderException { Failure: ChatProviderFailure.Authentication } => new AnswerFailedException(
                AnswerFailureKind.Authentication,
                "The API rejected the key (401). Check the key file named by DocQaMs:ApiKeyPath.",
                exception),
            ChatProviderException { Failure: ChatProviderFailure.Forbidden } => new AnswerFailedException(
                AnswerFailureKind.Authentication,
                "The key is not permitted to make this request (403).",
                exception),
            ChatProviderException { Failure: ChatProviderFailure.RateLimited } => new AnswerFailedException(
                AnswerFailureKind.RateLimited,
                "Rate limited or out of quota (429). Wait a minute, then ask again.",
                exception),
            ChatProviderException { Failure: ChatProviderFailure.ApiError } api => new AnswerFailedException(
                AnswerFailureKind.ApiError,
                $"The API returned an error{StatusAndErrorType(api)}. Ask again; if it persists, see the log.",
                exception),
            ChatProviderException { Failure: ChatProviderFailure.StreamError } stream => new AnswerFailedException(
                AnswerFailureKind.ApiError,
                $"The API reported an error partway through the answer{ErrorTypeSuffix(stream)}. Ask again.",
                exception),
            ChatProviderException { Failure: ChatProviderFailure.StreamEndedEarly } => new AnswerFailedException(
                AnswerFailureKind.StreamEndedEarly,
                "The answer stream ended before the answer was complete. Ask again.",
                exception),
            ChatProviderException { Failure: ChatProviderFailure.Connection } or IOException or HttpRequestException => new AnswerFailedException(
                AnswerFailureKind.Connection,
                "Network error: the connection to the API failed. Check the connection, then ask again.",
                exception),
            NotSupportedException => new AnswerFailedException(
                AnswerFailureKind.Unexpected,
                "The response contained a citation or block this app does not handle. See the log.",
                exception),
            _ => new AnswerFailedException(
                AnswerFailureKind.Unexpected,
                "The answer failed unexpectedly. See the log.",
                exception),
        };
    }

    /// <summary>
    /// <c> (529, OverloadedError)</c>: the status code and error type in parentheses, whichever are known;
    /// empty when neither is.
    /// </summary>
    private static string StatusAndErrorType(ChatProviderException exception)
    {
        string? type = ErrorTypeName(exception.ErrorType);
        return (exception.StatusCode, type) switch
        {
            ({ } status, { } name) => FormattableString.Invariant($" ({status}, {name})"),
            ({ } status, null) => FormattableString.Invariant($" ({status})"),
            (null, { } name) => $" ({name})",
            _ => "",
        };
    }

    /// <summary><c> (OverloadedError)</c>, or empty when the error type is not known.</summary>
    private static string ErrorTypeSuffix(ChatProviderException exception) =>
        ErrorTypeName(exception.ErrorType) is { } name ? $" ({name})" : "";

    /// <summary>
    /// The error type as the app has always shown it, in Pascal case: the API's <c>overloaded_error</c>
    /// reads <c>OverloadedError</c>. A name already in that form is kept.
    /// </summary>
    internal static string? ErrorTypeName(string? errorType)
    {
        if (string.IsNullOrWhiteSpace(errorType))
        {
            return null;
        }

        var name = new StringBuilder(errorType.Length);
        foreach (string word in errorType.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            name.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        return name.ToString();
    }

    private static string Duration(TimeSpan timeout) => timeout >= TimeSpan.FromMinutes(1)
        ? $"{timeout.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes"
        : $"{timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds";
}
