using System.Collections.Frozen;
using Anthropic;
using Anthropic.Core;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.ApiClient;

/// <summary>
/// Builds the app's one <see cref="AnthropicClient"/>, with an explicit key and base URL, and one
/// timeout view of it per effort level.
/// </summary>
public sealed class AnthropicClientProvider : IAnthropicClientProvider, IDisposable
{
    /// <summary>
    /// The API's base URL, fixed in code. The SDK otherwise reads <c>ANTHROPIC_BASE_URL</c>, so an
    /// ambient environment variable could redirect every request, key included.
    /// </summary>
    public const string ApiBaseUrl = "https://api.anthropic.com";

    /// <summary>
    /// Retries for failures before the response starts: 408, 409, 429, 5xx and connection errors.
    /// </summary>
    public const int MaxRetryCount = 2;

    private readonly AnthropicClient _client;
    private readonly FrozenDictionary<EffortLevel, IAnthropicClient> _views;

    private AnthropicClientProvider(AnthropicClient client, FrozenDictionary<EffortLevel, IAnthropicClient> views)
    {
        _client = client;
        _views = views;
    }

    /// <inheritdoc/>
    public IAnthropicClient Client => _client;

    /// <summary>
    /// Creates the client and its per-level views.
    /// </summary>
    /// <param name="apiKey">The API key, from <see cref="ApiKeyLoader"/>.</param>
    /// <param name="timeouts">The request timeout for each effort level.</param>
    /// <param name="handlers">
    /// HTTP handlers every request passes through, outermost first. Never a custom
    /// <see cref="HttpClient"/>: its own 100-second timeout would conflict with the SDK's.
    /// </param>
    /// <returns>The provider, which owns the client.</returns>
    /// <exception cref="ArgumentException">A selectable effort level has no timeout.</exception>
    public static AnthropicClientProvider Create(
        string apiKey,
        IReadOnlyDictionary<EffortLevel, TimeSpan> timeouts,
        IReadOnlyList<DelegatingHandler> handlers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(handlers);

        EffortLevel[] missing = CacheAffecting.EffortLevels.Where(level => !timeouts.ContainsKey(level)).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"No request timeout for effort level(s): {string.Join(", ", missing)}.", nameof(timeouts));
        }

        // Key and base URL are both explicit. An explicit key also turns off the SDK's profile and
        // credential auto-resolution, so an ambient ANTHROPIC_PROFILE cannot substitute another
        // account. It does not stop an ambient ANTHROPIC_AUTH_TOKEN, which the SDK still sends as a
        // bearer token, so the outermost handler removes it.
        var client = new AnthropicClient(new ClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = ApiBaseUrl,
            MaxRetries = MaxRetryCount,
            Handlers = [new ApiKeyOnlyHandler(), .. handlers],
        });

        // The SDK's Timeout covers the whole HTTP call, including reading the streamed body, so each
        // level needs its own: an answer at `max` can run for over 20 minutes.
        FrozenDictionary<EffortLevel, IAnthropicClient> views = CacheAffecting.EffortLevels.ToFrozenDictionary(
            level => level,
            level => client.WithOptions(options =>
            {
                options.Timeout = timeouts[level];
                return options;
            }));

        return new AnthropicClientProvider(client, views);
    }

    /// <inheritdoc/>
    public IAnthropicClient ForEffort(EffortLevel level) =>
        _views.TryGetValue(level, out IAnthropicClient? view)
            ? view
            : throw new ArgumentOutOfRangeException(nameof(level), level, "Not a selectable effort level.");

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();
}
