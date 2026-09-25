using Anthropic;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.ApiClient;

/// <summary>
/// Hands out the app's one Anthropic client, and its per-effort-level views.
/// </summary>
public interface IAnthropicClientProvider
{
    /// <summary>
    /// The client for short, non-streaming calls such as token counting, with the SDK's default
    /// timeout.
    /// </summary>
    IAnthropicClient Client { get; }

    /// <summary>
    /// A view of <see cref="Client"/> whose timeout covers a whole streamed answer at
    /// <paramref name="level"/>. The views share the client's connection pool and handlers.
    /// </summary>
    /// <param name="level">The effort level the answer is requested at.</param>
    /// <returns>The view for that level.</returns>
    IAnthropicClient ForEffort(EffortLevel level);
}
