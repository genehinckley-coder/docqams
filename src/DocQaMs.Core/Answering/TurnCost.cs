using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Answering;

/// <summary>
/// What a response cost in USD, per billing bucket, at the configured prices. For display only.
/// </summary>
/// <param name="Input">Uncached input.</param>
/// <param name="CacheWrite1h">Cache writes with the 1-hour TTL.</param>
/// <param name="CacheWrite5m">Cache writes with the 5-minute TTL.</param>
/// <param name="CacheRead">Cache reads.</param>
/// <param name="Output">Output, thinking included.</param>
public sealed record TurnCost(decimal Input, decimal CacheWrite1h, decimal CacheWrite5m, decimal CacheRead, decimal Output)
{
    private const decimal TokensPerMillion = 1_000_000m;

    /// <summary>No cost.</summary>
    public static TurnCost Zero { get; } = new(0m, 0m, 0m, 0m, 0m);

    /// <summary>The sum of the five buckets.</summary>
    public decimal Total => Input + CacheWrite1h + CacheWrite5m + CacheRead + Output;

    /// <summary>
    /// Prices each bucket of <paramref name="usage"/> from <paramref name="prices"/>, the price row of
    /// the model that produced it.
    /// </summary>
    /// <param name="usage">The tokens.</param>
    /// <param name="prices">The model's prices, in USD per million tokens.</param>
    /// <returns>The cost per bucket.</returns>
    public static TurnCost For(TokenUsage usage, ModelPrices prices)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(prices);

        return new TurnCost(
            usage.Input * prices.InputPerMTok / TokensPerMillion,
            usage.CacheWrite1h * prices.CacheWrite1hPerMTok / TokensPerMillion,
            usage.CacheWrite5m * prices.CacheWrite5mPerMTok / TokensPerMillion,
            usage.CacheRead * prices.CacheReadPerMTok / TokensPerMillion,
            usage.Output * prices.OutputPerMTok / TokensPerMillion);
    }

    /// <summary>Adds two costs bucket by bucket, for a conversation's or the app's totals.</summary>
    /// <param name="other">The cost to add.</param>
    /// <returns>The sum.</returns>
    public TurnCost Add(TurnCost other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new TurnCost(
            Input + other.Input,
            CacheWrite1h + other.CacheWrite1h,
            CacheWrite5m + other.CacheWrite5m,
            CacheRead + other.CacheRead,
            Output + other.Output);
    }
}
