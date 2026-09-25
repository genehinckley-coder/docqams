using System.Text.Json;
using Anthropic.Models.Messages;

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
    /// Reads the SDK's usage.
    /// </summary>
    /// <remarks>
    /// A request carries a 1-hour and a 5-minute breakpoint. Any part of the write the TTL breakdown does
    /// not account for, all of it when the breakdown is missing, is counted at the 1-hour TTL: the higher
    /// price, and the one the corpus pays, so the cost errs high rather than low. Plan item 1 measured the
    /// breakdown as present in practice; this is the defensive path, as in the prototype. The breakdown is
    /// read from its JSON, so a missing field reads as unknown rather than as zero.
    /// </remarks>
    internal static TokenUsage From(Usage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        long written = usage.CacheCreationInputTokens ?? 0;
        long? write1h = Count(usage.CacheCreation, "ephemeral_1h_input_tokens");
        long write5m = Count(usage.CacheCreation, "ephemeral_5m_input_tokens") ?? 0;
        return new TokenUsage(
            usage.InputTokens,
            Math.Max(write1h ?? 0, written - write5m),
            write5m,
            usage.CacheReadInputTokens ?? 0,
            usage.OutputTokens);
    }

    private static long? Count(CacheCreation? breakdown, string field) =>
        breakdown is not null
        && breakdown.RawData.TryGetValue(field, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long count)
            ? count
            : null;

    /// <summary>
    /// Reads the input side of the usage in <c>message_start</c>. Its output count is a placeholder, since
    /// output is reported only when the response ends, so it is set to zero.
    /// </summary>
    internal static TokenUsage InputSide(Usage usage) => From(usage) with { Output = 0 };
}
