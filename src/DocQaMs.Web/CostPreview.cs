using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;

namespace DocQaMs.Web;

/// <summary>The cost preview under the model and level selects, one sentence per part.</summary>
/// <param name="Summary">Whether the corpus cache is cold or probably warm, and what a question then costs.</param>
/// <param name="WorstCase">What a question costs if its answer runs to the level's output limit. Always shown.</param>
/// <param name="Caution">The caution about levels below High, or <see langword="null"/> at High and above.</param>
/// <param name="Warning">
/// The warning that another conversation is writing the same cache entry, or <see langword="null"/> when none is.
/// </param>
public sealed record CostPreviewText(string Summary, string WorstCase, string? Caution, string? Warning);

/// <summary>
/// Words the figures of a <see cref="CostEstimate"/> and the tracker's <see cref="CacheWarmth"/> for the preview.
/// For display only: every figure comes from the estimator, which computes it from the price table and output ranges.
/// </summary>
internal static class CostPreview
{
    /// <summary>Shown below High: a cheaper answer can match every quote and still reason less thoroughly.</summary>
    public const string LowLevelCaution = "Lower levels may miss related provisions. Quotes are still checked.";

    /// <summary>Builds the preview.</summary>
    /// <param name="estimate">The figures for the model and level.</param>
    /// <param name="warmth">The tracker's prediction for that model and level's corpus cache entry.</param>
    /// <param name="zone">The viewer's time zone, for the time a warm entry is expected to go cold.</param>
    /// <param name="followUp">Whether the conversation already has a committed answer, so the next question is not its first.</param>
    /// <returns>The preview's sentences.</returns>
    public static CostPreviewText Describe(CostEstimate estimate, CacheWarmth warmth, TimeZoneInfo zone, bool followUp)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(warmth);
        ArgumentNullException.ThrowIfNull(zone);

        string settings = Formatting.Settings(estimate.Settings);

        // Only Opus 5 at High is measured. Estimates and assumptions both read "estimated" until plan item 12
        // measures them; the Corpus settings screen gives each row's basis.
        string basis = estimate.IsMeasured ? "measured" : "estimated";
        string warm = Formatting.Money(estimate.WarmQuestion);

        string summary = warmth is { Status: CacheWarmthStatus.ProbablyWarm, WarmUntil: { } until }
            ? $"{settings}: probably warm until {Formatting.Time(until, zone)}, so about {warm} per question ({basis})."
            : $"{settings}: cold, so {Formatting.Money(estimate.ColdQuestion)} for the {(followUp ? "next" : "first")} " +
              $"question ({basis}), then about {warm} each.";

        string? warning = warmth.Status == CacheWarmthStatus.ColdWriteInProgress
            ? $"A cold write is in progress in another conversation. Asking now may pay a second " +
              $"{Formatting.Money(estimate.ColdInput)} write; wait until that answer starts streaming."
            : null;

        // Warmth is only ever probable, so the worst case assumes a cold write too.
        string worstCase = $"Up to {Formatting.Money(estimate.WorstCaseCold)} if an answer runs to its output limit.";

        string? caution = estimate.Settings.Effort < EffortLevel.High ? LowLevelCaution : null;
        return new CostPreviewText(summary, worstCase, caution, warning);
    }
}
