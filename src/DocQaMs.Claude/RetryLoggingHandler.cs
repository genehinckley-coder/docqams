using Microsoft.Extensions.Logging;

namespace DocQaMs.Claude;

/// <summary>
/// Logs every failed attempt the SDK retries: a retryable status, or a connection error.
/// </summary>
/// <remarks>
/// <para>
/// Installed through <c>ClientOptions.Handlers</c>, beneath the SDK's retry loop, so it sees each
/// attempt as a separate request. The SDK sends no retry-count header, so the handler logs the
/// failure that triggers a retry rather than the retry itself.
/// </para>
/// <para>
/// Only the method, path, status and response request-id are logged. Never a request header, which
/// carries the API key, and never a body, which carries about 480K characters of corpus.
/// </para>
/// </remarks>
/// <param name="logger">Receives one warning per retryable failure.</param>
public sealed class RetryLoggingHandler(ILogger<RetryLoggingHandler> logger) : DelegatingHandler
{
    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "Anthropic {Method} {Path} failed before a response arrived. The SDK retries connection errors.",
                request.Method,
                request.RequestUri?.AbsolutePath);
            throw;
        }

        if (IsRetryableStatus((int)response.StatusCode))
        {
            logger.LogWarning(
                "Anthropic {Method} {Path} returned {StatusCode} (request-id {RequestId}). The SDK retries this status.",
                request.Method,
                request.RequestUri?.AbsolutePath,
                (int)response.StatusCode,
                response.Headers.TryGetValues("request-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null);
        }

        return response;
    }

    /// <summary>
    /// Whether the SDK retries a response with this status: 408, 409, 429 and every 5xx, per the
    /// documentation of <c>ClientOptions.MaxRetries</c>.
    /// </summary>
    internal static bool IsRetryableStatus(int statusCode) => statusCode is 408 or 409 or 429 or >= 500;
}
