using Anthropic.Models.Messages;

namespace DocQaMs.Core.Configuration;

/// <summary>
/// The five values of the request's <c>output_config.effort</c>, lowest to highest.
/// </summary>
/// <remarks>
/// Effort is part of the corpus cache key: each level has its own 1-hour corpus entry.
/// </remarks>
public enum EffortLevel
{
    /// <summary><c>low</c>: the cheapest level; may under-think multi-step questions.</summary>
    Low,

    /// <summary><c>medium</c>.</summary>
    Medium,

    /// <summary><c>high</c>: the prototype's level, and the default.</summary>
    High,

    /// <summary><c>xhigh</c>.</summary>
    XHigh,

    /// <summary><c>max</c>: the most thorough level, with the longest and most expensive answers.</summary>
    Max,
}

/// <summary>
/// Conversions for <see cref="EffortLevel"/>.
/// </summary>
public static class EffortLevelExtensions
{
    /// <summary>
    /// Returns the string the Messages API expects in <c>output_config.effort</c>.
    /// </summary>
    /// <param name="level">The effort level.</param>
    /// <returns>One of <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined value.</exception>
    public static string ToWireValue(this EffortLevel level) => level switch
    {
        EffortLevel.Low => "low",
        EffortLevel.Medium => "medium",
        EffortLevel.High => "high",
        EffortLevel.XHigh => "xhigh",
        EffortLevel.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a defined effort level."),
    };

    /// <summary>
    /// Returns the level's name as the UI shows it.
    /// </summary>
    /// <param name="level">The effort level.</param>
    /// <returns>One of <c>Low</c>, <c>Medium</c>, <c>High</c>, <c>Extra high</c> or <c>Max</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined value.</exception>
    public static string ToDisplayName(this EffortLevel level) => level switch
    {
        EffortLevel.Low => "Low",
        EffortLevel.Medium => "Medium",
        EffortLevel.High => "High",
        EffortLevel.XHigh => "Extra high",
        EffortLevel.Max => "Max",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a defined effort level."),
    };

    /// <summary>
    /// Returns the SDK's value for <c>output_config.effort</c>.
    /// </summary>
    /// <param name="level">The effort level.</param>
    /// <returns>The matching <see cref="Effort"/> member; it serializes to <see cref="ToWireValue"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined value.</exception>
    public static Effort ToSdkEffort(this EffortLevel level) => level switch
    {
        EffortLevel.Low => Effort.Low,
        EffortLevel.Medium => Effort.Medium,
        EffortLevel.High => Effort.High,
        EffortLevel.XHigh => Effort.Xhigh,
        EffortLevel.Max => Effort.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a defined effort level."),
    };
}
