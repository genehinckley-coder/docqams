using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.Options;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Conversations;

/// <summary>Where a cost estimate's output figures come from, as the plan's Cost Reference labels them.</summary>
public enum EstimateBasis
{
    /// <summary>Measured on the combination itself: an entry in <see cref="DocQaOptions.MeasuredOutputTokens"/>.</summary>
    Measured,

    /// <summary>An estimate derived from published effort curves: <c>low</c> and <c>medium</c>.</summary>
    Estimate,

    /// <summary>An assumption with no published figure behind it.</summary>
    Assumption,
}

/// <summary>A range of costs in USD.</summary>
/// <param name="Lowest">The low end.</param>
/// <param name="Highest">The high end.</param>
public sealed record CostRange(decimal Lowest, decimal Highest);

/// <summary>
/// What a question is expected to cost at one model and level, in USD, computed from the price table, the
/// output limits and the estimated output ranges. For display only.
/// </summary>
/// <param name="Settings">The model and level.</param>
/// <param name="Basis">How far the output figures can be trusted.</param>
/// <param name="ColdInput">The corpus written to the cache at the 1-hour rate: the input side of a cold question.</param>
/// <param name="WarmInput">The corpus read from the cache: the input side of a warm question.</param>
/// <param name="ColdQuestion">A cold question: the corpus write plus the estimated output.</param>
/// <param name="WarmQuestion">
/// A warm question: from a first question (the corpus read plus the lowest output) to a follow-up (the corpus
/// read, a tail write of a previous answer at the high end, and the highest output).
/// </param>
/// <param name="WorstCaseCold">A cold question whose answer runs to the level's output limit.</param>
/// <param name="WorstCaseFollowUp">A follow-up after a capped answer: the corpus read, a tail write of that answer, and another capped answer.</param>
/// <param name="OutputLimit">The level's <c>max_tokens</c>.</param>
public sealed record CostEstimate(
    AnswerSettings Settings,
    EstimateBasis Basis,
    decimal ColdInput,
    decimal WarmInput,
    CostRange ColdQuestion,
    CostRange WarmQuestion,
    decimal WorstCaseCold,
    decimal WorstCaseFollowUp,
    int OutputLimit)
{
    /// <summary>Whether the output figures are measured rather than estimated or assumed.</summary>
    public bool IsMeasured => Basis == EstimateBasis.Measured;
}

/// <summary>
/// Computes the figures behind the model and level selector's cost preview, and the "up to about" figure for a
/// turn stopped before its usage arrived.
/// </summary>
/// <remarks>
/// Every figure follows from <see cref="DocQaOptions.Prices"/>, <see cref="DocQaOptions.MaxTokens"/>,
/// <see cref="DocQaOptions.MeasuredOutputTokens"/> and <see cref="DocQaOptions.EstimatedOutputTokens"/>, so a new
/// measurement changes the preview by changing configuration only. The question's own few dozen tokens are left out, as in the Cost Reference.
/// </remarks>
public sealed class CostEstimator
{
    private const decimal TokensPerMillion = 1_000_000m;

    private readonly DocQaOptions _options;

    /// <summary>Creates the estimator.</summary>
    /// <param name="options">The prices, output limits and estimated output ranges.</param>
    public CostEstimator(IOptions<DocQaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>
    /// The basis of a combination's output figures: measured where <see cref="DocQaOptions.MeasuredOutputTokens"/>
    /// has a range for it; otherwise <c>low</c> and <c>medium</c> are estimates and everything else is an assumption.
    /// </summary>
    /// <param name="settings">The model and level.</param>
    /// <returns>The basis.</returns>
    public EstimateBasis BasisOf(AnswerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MeasuredRange(settings) is not null
            ? EstimateBasis.Measured
            : settings.Effort is EffortLevel.Low or EffortLevel.Medium ? EstimateBasis.Estimate : EstimateBasis.Assumption;
    }

    /// <summary>The estimate for a question about <paramref name="corpus"/>.</summary>
    /// <param name="settings">The model and level.</param>
    /// <param name="corpus">
    /// The corpus. Its token count on the selected model includes the system prompt and a placeholder question,
    /// a few tokens more than the cached prefix, so the figures err slightly high.
    /// </param>
    /// <returns>The estimate.</returns>
    public CostEstimate Estimate(AnswerSettings settings, CorpusSnapshot corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(settings);
        long tokens = corpus.TokenCounts.TryGetValue(settings.ModelId, out long count) ? count : corpus.LargestTokenCount;
        return Estimate(settings, tokens);
    }

    /// <summary>The estimate for a question whose cached corpus prefix is <paramref name="corpusTokens"/> tokens.</summary>
    /// <param name="settings">The model and level.</param>
    /// <param name="corpusTokens">The corpus prefix, in tokens.</param>
    /// <returns>The estimate.</returns>
    public CostEstimate Estimate(AnswerSettings settings, long corpusTokens)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(corpusTokens);

        ModelPrices prices = _options.Prices[settings.ModelId];
        OutputTokenRange output = MeasuredRange(settings) ?? _options.EstimatedOutputTokens[settings.Effort];
        int limit = _options.MaxTokens[settings.Effort];

        decimal coldInput = Price(corpusTokens, prices.CacheWrite1hPerMTok);
        decimal warmInput = Price(corpusTokens, prices.CacheReadPerMTok);
        decimal Output(long tokens) => Price(tokens, prices.OutputPerMTok);

        // A follow-up's tail is roughly the previous turn's output, written at the 5-minute rate.
        decimal Tail(long tokens) => Price(tokens, prices.CacheWrite5mPerMTok);

        return new CostEstimate(
            settings,
            BasisOf(settings),
            coldInput,
            warmInput,
            new CostRange(coldInput + Output(output.Lowest), coldInput + Output(output.Highest)),
            new CostRange(warmInput + Output(output.Lowest), warmInput + Tail(output.Highest) + Output(output.Highest)),
            coldInput + Output(limit),
            warmInput + Tail(limit) + Output(limit),
            limit);
    }

    private OutputTokenRange? MeasuredRange(AnswerSettings settings) =>
        _options.MeasuredOutputTokens?.GetValueOrDefault(settings.ModelId)?.GetValueOrDefault(settings.Effort);

    private static decimal Price(long tokens, decimal perMillion) => tokens * perMillion / TokensPerMillion;
}
