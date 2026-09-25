using System.Net;
using System.Text;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// The innermost HTTP handler in a test: records each request and answers it without touching the
/// network.
/// </summary>
/// <param name="respond">Builds the response to the nth request, counting from 1.</param>
internal sealed class StubHandler(Func<int, HttpResponseMessage> respond) : DelegatingHandler
{
    private readonly List<CapturedRequest> _requests = [];

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedRequest captured = await CapturedRequest.FromAsync(request, cancellationToken);
        int number;
        lock (_requests)
        {
            _requests.Add(captured);
            number = _requests.Count;
        }

        HttpResponseMessage response = respond(number);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>
/// Records each request and passes it on to the network. For live tests, which need to see what the
/// SDK actually sent.
/// </summary>
internal sealed class CaptureHandler : DelegatingHandler
{
    private readonly List<CapturedRequest> _requests = [];

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedRequest captured = await CapturedRequest.FromAsync(request, cancellationToken);
        lock (_requests)
        {
            _requests.Add(captured);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>What a test needs to know about a request, captured before it is disposed.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? ApiKey,
    bool HasAuthorization,
    IReadOnlyList<(string Name, string Value)> Headers,
    string? Body)
{
    public static async Task<CapturedRequest> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken) => new(
        request.Method,
        request.RequestUri,
        request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? keys) ? keys.Single() : null,
        request.Headers.Contains("Authorization"),
        [.. request.Headers.SelectMany(header => header.Value.Select(value => (header.Key, value)))],
        request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
}
