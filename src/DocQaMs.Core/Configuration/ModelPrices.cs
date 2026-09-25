namespace DocQaMs.Core.Configuration;

/// <summary>
/// One model's prices in USD per million tokens, one per billing bucket.
/// </summary>
public sealed class ModelPrices
{
    /// <summary>Uncached input.</summary>
    public decimal InputPerMTok { get; set; }

    /// <summary>Cache writes with a 5-minute TTL: the conversation tail.</summary>
    public decimal CacheWrite5mPerMTok { get; set; }

    /// <summary>Cache writes with a 1-hour TTL: the corpus.</summary>
    public decimal CacheWrite1hPerMTok { get; set; }

    /// <summary>Cache reads.</summary>
    public decimal CacheReadPerMTok { get; set; }

    /// <summary>Output, including thinking.</summary>
    public decimal OutputPerMTok { get; set; }
}
