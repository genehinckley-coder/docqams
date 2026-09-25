using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>
/// A response's tokens in the five buckets it is billed by.
/// </summary>
/// <remarks>
/// <see cref="Input"/> excludes cached tokens, so the three input buckets are separate: priced as one, a
/// cold call would be under-reported about tenfold.
/// </remarks>
/// <param name="Input">Uncached input tokens.</param>
/// <param name="CacheWrite1h">Tokens written to the cache with the 1-hour TTL: the corpus prefix.</param>
/// <param name="CacheWrite5m">Tokens written with the 5-minute TTL: the conversation tail.</param>
/// <param name="CacheRead">Tokens read from the cache.</param>
/// <param name="Output">Output tokens, thinking included. Zero until the response ends.</param>
public sealed record TokenUsage(long Input, long CacheWrite1h, long CacheWrite5m, long CacheRead, long Output)
{
    /// <summary>All cache writes, at either TTL.</summary>
    public long CacheWrite => CacheWrite1h + CacheWrite5m;

    /// <summary>Whether the response wrote or read any cache entry.</summary>
    public bool HasCacheActivity => CacheWrite > 0 || CacheRead > 0;

    /// <summary>The request's size in the context window: input plus cache writes plus cache reads.</summary>
    public long ContextTokens => Input + CacheWrite + CacheRead;

    /// <summary>
    /// Reads a provider's usage, in the Microsoft.Extensions.AI convention.
    /// </summary>
    /// <remarks>
    /// <see cref="UsageDetails.InputTokenCount"/> counts every input token: uncached, cache reads and cache
    /// writes. The cache reads are <see cref="UsageDetails.CachedInputTokenCount"/>, and the writes are split
    /// by TTL under <see cref="ChatKeys.CacheWrite1hTokens"/> and <see cref="ChatKeys.CacheWrite5mTokens"/>,
    /// so the uncached input is what remains. A missing count reads as zero, and the remainder never goes
    /// below zero. The provider's chat client counts any part of a write its API does not break down by TTL
    /// at the 1-hour TTL: the higher price, and the one the corpus pays, so the cost errs high rather than low.
    /// </remarks>
    /// <param name="usage">The usage.</param>
    /// <returns>The usage in billing buckets.</returns>
    internal static TokenUsage From(UsageDetails usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        long write1h = Count(usage, ChatKeys.CacheWrite1hTokens);
        long write5m = Count(usage, ChatKeys.CacheWrite5mTokens);
        long read = usage.CachedInputTokenCount ?? 0;
        long input = Math.Max(0, (usage.InputTokenCount ?? 0) - read - write1h - write5m);
        return new TokenUsage(input, write1h, write5m, read, usage.OutputTokenCount ?? 0);
    }

    private static long Count(UsageDetails usage, string key) =>
        usage.AdditionalCounts is { } counts && counts.TryGetValue(key, out long count) ? count : 0;

    /// <summary>
    /// Reads the input side of the usage, as the provider reports it when the response starts. Its output
    /// count is a placeholder, since output is reported only when the response ends, so it is set to zero.
    /// </summary>
    /// <param name="usage">The usage.</param>
    /// <returns>The usage in billing buckets, with no output.</returns>
    internal static TokenUsage InputSide(UsageDetails usage) => From(usage) with { Output = 0 };
}
