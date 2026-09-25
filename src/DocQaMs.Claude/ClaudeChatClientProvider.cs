using System.Collections.Frozen;
using Anthropic;
using Anthropic.Core;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Claude;

/// <summary>
/// Builds the app's one <see cref="AnthropicClient"/>, with an explicit key and base URL, and one chat
/// client per effort level over a timeout view of it.
/// </summary>
/// <remarks>
/// Each level's chat client is the pipeline <see cref="ClaudeChatClient"/> → OpenTelemetry → the SDK's
/// adapter over that level's view. <see cref="ChatClientBuilder"/> applies its middleware in the order
/// added, outermost first, so the Claude middleware sits outside the telemetry: the telemetry records the
/// adapter's own single usage per response rather than the Claude middleware's two (input side, then
/// final), which it would sum. Sensitive data (prompts and replies) is never recorded.
/// </remarks>
public sealed class ClaudeChatClientProvider : IChatClientProvider, IDisposable
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

    /// <summary>The OpenTelemetry source name the chat clients report under.</summary>
    public const string TelemetrySourceName = "DocQaMs.AI";

    private readonly AnthropicClient _client;
    private readonly FrozenDictionary<EffortLevel, IChatClient> _chatClients;

    private ClaudeChatClientProvider(AnthropicClient client, FrozenDictionary<EffortLevel, IChatClient> chatClients)
    {
        _client = client;
        _chatClients = chatClients;
    }

    /// <summary>
    /// The underlying client, with the SDK's default timeout, for short non-streaming calls such as token
    /// counting.
    /// </summary>
    public IAnthropicClient AnthropicClient => _client;

    /// <summary>
    /// Creates the client and its per-level chat clients.
    /// </summary>
    /// <param name="apiKey">The API key, from <see cref="ApiKeyLoader"/>.</param>
    /// <param name="timeouts">The request timeout for each effort level.</param>
    /// <param name="handlers">
    /// HTTP handlers every request passes through, outermost first. Never a custom
    /// <see cref="HttpClient"/>: its own 100-second timeout would conflict with the SDK's.
    /// </param>
    /// <param name="loggerFactory">
    /// Logs the Claude middleware's warnings; <see langword="null"/> for none.
    /// </param>
    /// <returns>The provider, which owns the client.</returns>
    /// <exception cref="ArgumentException">A selectable effort level has no timeout.</exception>
    public static ClaudeChatClientProvider Create(
        string apiKey,
        IReadOnlyDictionary<EffortLevel, TimeSpan> timeouts,
        IReadOnlyList<DelegatingHandler> handlers,
        ILoggerFactory? loggerFactory = null)
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

        ILogger? logger = loggerFactory?.CreateLogger<ClaudeChatClient>();

        // The SDK's Timeout covers the whole HTTP call, including reading the streamed body, so each
        // level needs its own: an answer at `max` can run for over 20 minutes. The views share the
        // client's connection pool and handlers.
        FrozenDictionary<EffortLevel, IChatClient> chatClients = CacheAffecting.EffortLevels.ToFrozenDictionary(
            level => level,
            level =>
            {
                IAnthropicClient view = client.WithOptions(options =>
                {
                    options.Timeout = timeouts[level];
                    return options;
                });

                return new ChatClientBuilder(view.AsIChatClient())
                    .Use(inner => new ClaudeChatClient(inner, logger))
                    // No logger factory: the telemetry would log every failure again, and every Stop, as a
                    // warning with a stack trace. The app logs its own outcomes; traces and metrics are kept.
                    .UseOpenTelemetry(
                        loggerFactory: null,
                        sourceName: TelemetrySourceName,
                        configure: telemetry => telemetry.EnableSensitiveData = false)
                    .Build();
            });

        return new ClaudeChatClientProvider(client, chatClients);
    }

    /// <inheritdoc/>
    public IChatClient ForEffort(EffortLevel level) =>
        _chatClients.TryGetValue(level, out IChatClient? chatClient)
            ? chatClient
            : throw new ArgumentOutOfRangeException(nameof(level), level, "Not a selectable effort level.");

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (IChatClient chatClient in _chatClients.Values)
        {
            chatClient.Dispose();
        }

        _client.Dispose();
    }
}
