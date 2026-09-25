using System.Globalization;
using Anthropic.Exceptions;
using Anthropic.Models;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Maps what can go wrong during an answer to an <see cref="AnswerFailedException"/>, most specific first.
/// </summary>
/// <remarks>
/// Plan item 1 found three failures that are not <c>Anthropic.Exceptions</c> API types: a body that breaks
/// after the response started (a raw <see cref="IOException"/>), a body that ends without
/// <c>message_stop</c> (<see cref="AnthropicInvalidDataException"/>), and the SDK's own timeout on the
/// response headers (a <see cref="TaskCanceledException"/> with the caller's token not cancelled).
/// Stop is not a failure and never reaches this mapping.
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
            AnthropicServiceException service when IsCreditExhausted(service) => new AnswerFailedException(
                AnswerFailureKind.CreditExhausted,
                "The API account is out of credit. Add credit in the Claude Console, then ask again.",
                exception),
            AnthropicUnauthorizedException => new AnswerFailedException(
                AnswerFailureKind.Authentication,
                "The API rejected the key (401). Check the key file named by DocQaMs:ApiKeyPath.",
                exception),
            AnthropicForbiddenException => new AnswerFailedException(
                AnswerFailureKind.Authentication,
                "The key is not permitted to make this request (403).",
                exception),
            AnthropicRateLimitException => new AnswerFailedException(
                AnswerFailureKind.RateLimited,
                "Rate limited or out of quota (429). Wait a minute, then ask again.",
                exception),
            AnthropicApiException api => new AnswerFailedException(
                AnswerFailureKind.ApiError,
                $"The API returned an error ({(int)api.StatusCode}{ErrorTypeSuffix(api)}). Ask again; if it persists, see the log.",
                exception),
            AnthropicSseException sse => new AnswerFailedException(
                AnswerFailureKind.ApiError,
                $"The API reported an error partway through the answer{ErrorTypeSuffix(sse, parenthesized: true)}. Ask again.",
                exception),
            AnthropicInvalidDataException => new AnswerFailedException(
                AnswerFailureKind.StreamEndedEarly,
                "The answer stream ended before the answer was complete. Ask again.",
                exception),
            AnthropicIOException or IOException or HttpRequestException => new AnswerFailedException(
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
    /// Exhausted credit arrives as a <c>billing_error</c>, or as a 400 whose message says the credit
    /// balance is too low. It is named separately because asking again cannot help.
    /// </summary>
    private static bool IsCreditExhausted(AnthropicServiceException exception)
    {
        if (exception.ErrorType == ErrorType.BillingError)
        {
            return true;
        }

        string? body = (exception as AnthropicApiException)?.ResponseBody;
        return exception is AnthropicBadRequestException
            && body is not null
            && (body.Contains("billing_error", StringComparison.Ordinal)
                || body.Contains("credit balance", StringComparison.OrdinalIgnoreCase));
    }

    private static string ErrorTypeSuffix(AnthropicServiceException exception, bool parenthesized = false)
    {
        if (exception.ErrorType is not { } type)
        {
            return "";
        }

        return parenthesized ? $" ({type})" : $", {type}";
    }

    private static string Duration(TimeSpan timeout) => timeout >= TimeSpan.FromMinutes(1)
        ? $"{timeout.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes"
        : $"{timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds";
}
