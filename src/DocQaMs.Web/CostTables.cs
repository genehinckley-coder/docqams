using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Web;

/// <summary>
/// One combination of model and effort level on the Corpus settings screen: what a question costs, how far
/// those figures can be trusted, and whether its corpus cache entry is predicted warm.
/// </summary>
/// <param name="Settings">The model and level, or <see langword="null"/> for a combination that is not offered.</param>
/// <param name="ModelId">The model id, whether or not the combination is offered.</param>
/// <param name="Effort">The effort level, whether or not the combination is offered.</param>
/// <param name="Estimate">Its figures, or <see langword="null"/> for a combination that is not offered.</param>
/// <param name="Warmth">Its cache prediction, or <see langword="null"/> for a combination that is not offered.</param>
/// <param name="ExcludedReason">Why it is not offered; <see langword="null"/> when it is.</param>
internal sealed record CostRow(
    AnswerSettings? Settings,
    string ModelId,
    EffortLevel Effort,
    CostEstimate? Estimate,
    CacheWarmth? Warmth,
    string? ExcludedReason)
{
    /// <summary>The combination as the table names it: <c>Opus 5, High</c>.</summary>
    public string Name => Settings is { } settings
        ? Formatting.Settings(settings)
        : Formatting.ShortName(CacheAffecting.SelectableModels.First(model => model.Id == ModelId)) + ", " + Effort.ToDisplayName();
}

/// <summary>
/// Builds the Corpus settings screen's cost tables, and words the parts of them that are not amounts.
/// </summary>
/// <remarks>
/// Every figure comes from <see cref="CostEstimator"/>, which computes it from the price table and the
/// estimated output ranges, so plan item 12's measurements reach this screen through configuration alone.
/// </remarks>
internal static class CostTables
{
    /// <summary>One row per combination, in the order the selects list them.</summary>
    /// <param name="estimator">The estimator.</param>
    /// <param name="tracker">The cache warmth tracker.</param>
    /// <param name="corpus">The corpus the figures price a question about.</param>
    /// <returns>Ten rows: every model crossed with every effort level.</returns>
    public static IReadOnlyList<CostRow> Rows(CostEstimator estimator, CacheWarmthTracker tracker, CorpusSnapshot corpus)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(corpus);

        List<CostRow> rows = [];
        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            foreach (EffortLevel effort in CacheAffecting.EffortLevels)
            {
                ExcludedCombination? excluded = CacheAffecting.ExcludedCombinations.FirstOrDefault(
                    combination => combination.ModelId == model.Id && combination.Effort == effort);
                if (excluded is not null)
                {
                    rows.Add(new CostRow(null, model.Id, effort, null, null, excluded.Reason));
                    continue;
                }

                AnswerSettings settings = AnswerSettings.Create(model.Id, effort);
                rows.Add(new CostRow(
                    settings,
                    model.Id,
                    effort,
                    estimator.Estimate(settings, corpus),
                    tracker.GetWarmth(CorpusCacheKey.For(corpus, settings)),
                    null));
            }
        }

        return rows.AsReadOnly();
    }

    /// <summary>How far a row's output figures can be trusted, as the plan's Cost Reference labels it.</summary>
    /// <param name="basis">The basis.</param>
    /// <returns>One word for the table's basis column.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="basis"/> is not a defined value.</exception>
    public static string BasisLabel(EstimateBasis basis) => basis switch
    {
        EstimateBasis.Measured => "Measured",
        EstimateBasis.Estimate => "Estimate",
        EstimateBasis.Assumption => "Assumption",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Not a defined basis."),
    };

    /// <summary>What the app believes about a combination's corpus cache entry, for the table's cache column.</summary>
    /// <param name="warmth">The prediction.</param>
    /// <param name="zone">The viewer's time zone, for the time a warm entry is expected to go cold.</param>
    /// <returns>A short phrase; "probably", since only this app's own requests are known.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="warmth"/> holds no defined status.</exception>
    public static string WarmthText(CacheWarmth warmth, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(warmth);
        ArgumentNullException.ThrowIfNull(zone);

        return warmth switch
        {
            { Status: CacheWarmthStatus.ProbablyWarm, WarmUntil: { } until } => $"Probably warm until {Formatting.Time(until, zone)}",
            { Status: CacheWarmthStatus.ProbablyWarm } => "Probably warm",
            { Status: CacheWarmthStatus.ColdWriteInProgress } => "Cold; a write is in progress",
            { Status: CacheWarmthStatus.Cold } => "Cold",
            _ => throw new ArgumentOutOfRangeException(nameof(warmth), warmth.Status, "Not a defined warmth status."),
        };
    }

    /// <summary>
    /// When the earliest of the rows' warm predictions turns cold, so the screen can refresh exactly then rather
    /// than poll.
    /// </summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The earliest expiry, or <see langword="null"/> when no row is predicted warm.</returns>
    public static DateTimeOffset? NextExpiry(IEnumerable<CostRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Select(row => row.Warmth?.WarmUntil).Where(until => until is not null).Min();
    }

    /// <summary>A signature of what the rows render as, so an unchanged screen is not re-rendered.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>A string that changes whenever any row's prediction does.</returns>
    public static string Signature(IEnumerable<CostRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return string.Join('|', rows.Select(row => $"{row.ModelId}:{row.Effort}:{row.Warmth?.Status}:{row.Warmth?.WarmUntil:O}"));
    }
}
