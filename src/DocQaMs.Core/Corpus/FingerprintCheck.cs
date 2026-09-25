using System.Globalization;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Corpus;

/// <summary>How the corpus compares with the one the previous load recorded.</summary>
public enum FingerprintStatus
{
    /// <summary>There is no state file: nothing to compare with.</summary>
    FirstRun,

    /// <summary>The fingerprint equals the recorded one.</summary>
    Unchanged,

    /// <summary>The fingerprint differs from the recorded one; the details name what changed.</summary>
    Changed,

    /// <summary>The state file could not be read or is malformed; the details say why.</summary>
    Unreadable,
}

/// <summary>
/// The result of comparing a corpus with the fingerprint the previous load recorded.
/// </summary>
/// <param name="Status">How the two compare.</param>
/// <param name="PreviousRecordedAt">When the previous load recorded its fingerprint (ISO 8601, UTC), if known.</param>
/// <param name="Details">
/// One line per change when <see cref="FingerprintStatus.Changed"/>; why the state file could not be used
/// when <see cref="FingerprintStatus.Unreadable"/>; otherwise empty.
/// </param>
public sealed record FingerprintCheck(FingerprintStatus Status, string? PreviousRecordedAt, IReadOnlyList<string> Details)
{
    internal static FingerprintCheck FirstRun { get; } = new(FingerprintStatus.FirstRun, null, []);

    /// <summary>The comparison as text for the user, with the cost of a change on each model.</summary>
    /// <param name="corpus">The corpus that was compared.</param>
    /// <param name="prices">Prices keyed by model id, from <see cref="DocQaOptions.Prices"/>.</param>
    /// <returns>One or more lines of text.</returns>
    public string Describe(Corpus corpus, IReadOnlyDictionary<string, ModelPrices> prices)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(prices);

        switch (Status)
        {
            case FingerprintStatus.FirstRun:
                return "No previous fingerprint on record; this one is now recorded.";
            case FingerprintStatus.Unchanged:
                return $"Unchanged since the load recorded at {PreviousRecordedAt}.";
            case FingerprintStatus.Unreadable:
                return $"The previous fingerprint could not be read ({string.Join("; ", Details)}), so a change in " +
                    "extraction output cannot be ruled out. This load's fingerprint replaces it.";
        }

        // A cached copy of the corpus is kept per model and effort level, and the write price is per model.
        IEnumerable<string> writeCosts = CacheAffecting.SelectableModels
            .Where(model => prices.ContainsKey(model.Id) && corpus.TokenCounts.ContainsKey(model.Id))
            .Select(model => string.Create(
                CultureInfo.InvariantCulture,
                $"${corpus.TokenCounts[model.Id] * prices[model.Id].CacheWrite1hPerMTok / 1_000_000m:F2} on {model.DisplayName}"));

        return string.Join(
            "\n",
            [
                $"The corpus changed since the load recorded at {PreviousRecordedAt}:",
                .. Details.Select(detail => $"  - {detail}"),
                "Likely causes: an edited source file, an AngleSharp upgrade, or an edit to the extractor or to a " +
                "CacheAffecting value.",
                "Consequence: any cached copy of the previous corpus no longer matches. The first question on each " +
                $"model and level writes the corpus cache afresh instead of reading it: about {string.Join(" and ", writeCosts)}.",
            ]);
    }
}
