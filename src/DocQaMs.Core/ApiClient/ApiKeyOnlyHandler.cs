namespace DocQaMs.Core.ApiClient;

/// <summary>
/// Removes any <c>Authorization</c> header, so that every request authenticates with the
/// <c>x-api-key</c> header alone.
/// </summary>
/// <remarks>
/// The SDK (12.47.0) sends an ambient <c>ANTHROPIC_AUTH_TOKEN</c> environment variable as a bearer
/// token even when an explicit API key is set, and an explicit empty <c>AuthToken</c> sends an empty
/// bearer token instead. Either would put a credential the app never chose on every request. This app
/// uses only the key from its key file, so the header never has a legitimate use.
/// </remarks>
internal sealed class ApiKeyOnlyHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = null;
        return base.SendAsync(request, cancellationToken);
    }
}
