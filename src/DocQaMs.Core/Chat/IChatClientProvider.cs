using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Chat;

/// <summary>
/// Hands out the app's chat clients: one per effort level, because each level has its own request timeout.
/// </summary>
/// <remarks>
/// Resolved lazily, when a request is about to be made, so a missing or malformed API key surfaces as a
/// failed answer (<see cref="FileNotFoundException"/> or <see cref="InvalidDataException"/> from the
/// factory) rather than stopping the app from starting.
/// </remarks>
public interface IChatClientProvider
{
    /// <summary>
    /// The chat client for answers at <paramref name="level"/>. Its whole-request timeout is
    /// <see cref="DocQaOptions.RequestTimeouts"/> for that level.
    /// </summary>
    /// <param name="level">A selectable effort level.</param>
    /// <returns>The chat client.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not selectable.</exception>
    IChatClient ForEffort(EffortLevel level);
}
