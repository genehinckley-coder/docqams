using System.Collections.Frozen;
using System.Globalization;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// The assembled corpus exceeds the token ceiling on at least one selectable model. It is never truncated.
/// </summary>
public sealed class CorpusTooLargeException : Exception
{
    /// <summary>Creates the exception, with a message that lists the options.</summary>
    /// <param name="tokenCounts">The corpus's token count on each selectable model, keyed by model id.</param>
    /// <param name="ceiling">The ceiling, from <see cref="DocQaOptions.TokenCeiling"/>.</param>
    public CorpusTooLargeException(IReadOnlyDictionary<string, long> tokenCounts, int ceiling)
        : base(BuildMessage(tokenCounts, ceiling))
    {
        TokenCounts = tokenCounts.ToFrozenDictionary(StringComparer.Ordinal);
        Ceiling = ceiling;
    }

    /// <summary>The corpus's token count on each selectable model, keyed by model id.</summary>
    public IReadOnlyDictionary<string, long> TokenCounts { get; }

    /// <summary>The ceiling the largest count exceeded.</summary>
    public int Ceiling { get; }

    private static string BuildMessage(IReadOnlyDictionary<string, long> tokenCounts, int ceiling)
    {
        ArgumentNullException.ThrowIfNull(tokenCounts);
        if (tokenCounts.Count == 0)
        {
            throw new ArgumentException("At least one token count is required.", nameof(tokenCounts));
        }

        string counts = string.Join(", ", tokenCounts
            .OrderBy(count => count.Key, StringComparer.Ordinal)
            .Select(count => string.Create(CultureInfo.InvariantCulture, $"{count.Value:N0} on {count.Key}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The assembled corpus is {tokenCounts.Values.Max():N0} tokens, over the {ceiling:N0}-token ceiling " +
            $"(DocQaMs:TokenCeiling); counted {counts}. Nothing was truncated, and nothing will be: answering " +
            $"from part of the corpus is the failure this guard exists to prevent. Options:\n" +
            $"  1. Remove documents: take files out of the corpus folder and their entries out of " +
            $"CacheAffecting.DocumentLabels.\n" +
            $"  2. Raise DocQaMs:TokenCeiling, if the context window still leaves room for conversation history " +
            $"and up to {DocQaOptions.ModelOutputTokenLimit:N0} tokens of output.\n" +
            $"  3. Re-plan: at this size, an agentic search over the documents is the right architecture.");
    }
}
