using Anthropic.Exceptions;
using Anthropic.Models;
using DocQaMs.Core.Chat;

namespace DocQaMs.Claude;

/// <summary>
/// Translates the Anthropic SDK's exceptions into <see cref="ChatProviderException"/>, most specific first.
/// </summary>
/// <remarks>
/// <para>
/// Ported from DocQa's <c>AnswerFailures</c>. Plan item 1 found three failures that are not
/// <c>Anthropic.Exceptions</c> API types: a body that breaks after the response started (a raw
/// <see cref="IOException"/>), a body that ends without <c>message_stop</c>
/// (<see cref="AnthropicInvalidDataException"/>), and the SDK's own timeout on the response headers (a
/// <see cref="TaskCanceledException"/> with the caller's token not cancelled). The first two are translated;
/// cancellation of any kind never is, so the app can tell Stop and timeouts apart itself.
/// </para>
/// <para>
/// <see cref="ChatProviderException.ErrorType"/> is the API's error type string (for example
/// <c>overloaded_error</c>), recovered from the SDK's <see cref="ErrorType"/>. DocQa showed the enum's name
/// (<c>OverloadedError</c>), which the app now derives from the API string. It is absent when the SDK did
/// not recognize the error type, as it was in DocQa.
/// </para>
/// </remarks>
internal static class ClaudeFailures
{
    /// <summary>
    /// Translates <paramref name="exception"/>, or returns <see langword="null"/> when it is not a provider
    /// failure this app recognizes and should propagate unchanged: cancellation, a bug, or an unsupported
    /// request or response.
    /// </summary>
    /// <param name="exception">What the SDK, or its transport, threw.</param>
    /// <returns>The translated exception, with <paramref name="exception"/> as its inner exception.</returns>
    public static ChatProviderException? Translate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        int? status = exception is AnthropicApiException api ? (int)api.StatusCode : null;
        string? errorType = (exception as AnthropicServiceException)?.ErrorType is { } type ? ToApiString(type) : null;

        ChatProviderFailure? failure = exception switch
        {
            OperationCanceledException => null,
            ChatProviderException => null,
            AnthropicServiceException service when IsCreditExhausted(service) => ChatProviderFailure.CreditExhausted,
            AnthropicUnauthorizedException => ChatProviderFailure.Authentication,
            AnthropicForbiddenException => ChatProviderFailure.Forbidden,
            AnthropicRateLimitException => ChatProviderFailure.RateLimited,
            AnthropicApiException => ChatProviderFailure.ApiError,
            AnthropicSseException => ChatProviderFailure.StreamError,
            AnthropicInvalidDataException => ChatProviderFailure.StreamEndedEarly,
            AnthropicIOException or IOException or HttpRequestException => ChatProviderFailure.Connection,
            _ => null,
        };

        // The SDK's message is kept as it is: it carries the API's own explanation (never the key or the
        // request body), and DocQa showed it verbatim for a failed token count.
        return failure is { } kind
            ? new ChatProviderException(kind, exception.Message, exception, status, errorType)
            : null;
    }

    /// <summary>
    /// The API's spelling of an error type: <see cref="ErrorType.OverloadedError"/> is <c>overloaded_error</c>.
    /// </summary>
    internal static string ToApiString(ErrorType type)
    {
        string name = type.ToString();
        var wire = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                wire.Append('_');
            }

            wire.Append(char.ToLowerInvariant(name[i]));
        }

        return wire.ToString();
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

}
