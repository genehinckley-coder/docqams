using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Tests.Conversations;

/// <summary>
/// Plan item 11: the cost preview's figures. Each is recomputed here from the options it is built from, never
/// copied from the plan's Cost Reference.
/// </summary>
public sealed class CostEstimatorTests
{
    private const long CorpusTokens = 199_745;

    private readonly DocQaOptions _options;

    private readonly CostEstimator _estimator;

    public CostEstimatorTests()
    {
        _options = TestSettings.ValidOptions();

        // Distinct values everywhere, so reading the wrong model, level or bucket shows.
        _options.Prices[ModelIds.ClaudeSonnet5] = new ModelPrices
        {
            InputPerMTok = 2m,
            CacheWrite5mPerMTok = 2.5m,
            CacheWrite1hPerMTok = 4m,
            CacheReadPerMTok = 0.2m,
            OutputPerMTok = 10m,
        };
        for (int i = 0; i < CacheAffecting.EffortLevels.Count; i++)
        {
            EffortLevel level = CacheAffecting.EffortLevels[i];
            _options.EstimatedOutputTokens[level] = new OutputTokenRange { Lowest = 800 * (i + 1), Highest = 3_000 * (i + 1) };
            _options.MaxTokens[level] = 16_000 * (i + 1);
        }

        _options.MeasuredOutputTokens[ModelIds.ClaudeOpus5] = new() { [EffortLevel.High] = new OutputTokenRange { Lowest = 3_100, Highest = 6_300 } };
        _options.MeasuredOutputTokens[ModelIds.ClaudeOpus55] = new() { [EffortLevel.High] = new OutputTokenRange { Lowest = 5_300, Highest = 8_300 } };

        _estimator = new CostEstimator(Options.Create(_options));
    }

    public static TheoryData<string, EffortLevel> Combinations()
    {
        TheoryData<string, EffortLevel> data = [];
        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            foreach (EffortLevel level in CacheAffecting.EffortLevels)
            {
                data.Add(model.Id, level);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryFigure_FollowsFromThePriceTableTheOutputRangeAndTheOutputLimit(string modelId, EffortLevel effort)
    {
        AnswerSettings settings = AnswerSettings.Create(modelId, effort);
        ModelPrices prices = _options.Prices[modelId];
        OutputTokenRange output = _options.MeasuredOutputTokens.GetValueOrDefault(modelId)?.GetValueOrDefault(effort)
            ?? _options.EstimatedOutputTokens[effort];
        int limit = _options.MaxTokens[effort];
        decimal PerMillion(long tokens, decimal price) => tokens * price / 1_000_000m;

        CostEstimate estimate = _estimator.Estimate(settings, CorpusTokens);

        decimal write = PerMillion(CorpusTokens, prices.CacheWrite1hPerMTok);
        decimal read = PerMillion(CorpusTokens, prices.CacheReadPerMTok);
        Assert.Equal(settings, estimate.Settings);
        Assert.Equal(write, estimate.ColdInput);
        Assert.Equal(read, estimate.WarmInput);
        Assert.Equal(
            new CostRange(write + PerMillion(output.Lowest, prices.OutputPerMTok), write + PerMillion(output.Highest, prices.OutputPerMTok)),
            estimate.ColdQuestion);

        // From a warm first question to a follow-up that also writes the previous answer as its tail.
        Assert.Equal(
            new CostRange(
                read + PerMillion(output.Lowest, prices.OutputPerMTok),
                read + PerMillion(output.Highest, prices.CacheWrite5mPerMTok) + PerMillion(output.Highest, prices.OutputPerMTok)),
            estimate.WarmQuestion);
        Assert.Equal(write + PerMillion(limit, prices.OutputPerMTok), estimate.WorstCaseCold);
        Assert.Equal(
            read + PerMillion(limit, prices.CacheWrite5mPerMTok) + PerMillion(limit, prices.OutputPerMTok),
            estimate.WorstCaseFollowUp);
        Assert.Equal(limit, estimate.OutputLimit);
    }

    [Fact]
    public void TheSameQuestion_IsPricedAtTheSelectedModelsPrices()
    {
        CostEstimate opus = _estimator.Estimate(AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.High), CorpusTokens);
        CostEstimate sonnet = _estimator.Estimate(AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.High), CorpusTokens);

        Assert.True(sonnet.ColdQuestion.Highest < opus.ColdQuestion.Highest);
        Assert.Equal(CorpusTokens * 4m / 1_000_000m, sonnet.ColdInput);
        Assert.Equal(CorpusTokens * 10m / 1_000_000m, opus.ColdInput);
    }

    [Theory]
    [InlineData(ModelIds.ClaudeOpus5, EffortLevel.High, EstimateBasis.Measured)]
    [InlineData(ModelIds.ClaudeOpus55, EffortLevel.High, EstimateBasis.Measured)]
    [InlineData(ModelIds.ClaudeOpus5, EffortLevel.Low, EstimateBasis.Estimate)]
    [InlineData(ModelIds.ClaudeOpus55, EffortLevel.Medium, EstimateBasis.Estimate)]
    [InlineData(ModelIds.ClaudeSonnet5, EffortLevel.Medium, EstimateBasis.Estimate)]
    [InlineData(ModelIds.ClaudeSonnet5, EffortLevel.High, EstimateBasis.Assumption)]
    [InlineData(ModelIds.ClaudeOpus5, EffortLevel.XHigh, EstimateBasis.Assumption)]
    [InlineData(ModelIds.ClaudeOpus55, EffortLevel.Max, EstimateBasis.Assumption)]
    public void OnlyTheConfiguredMeasurements_AreMeasured(string modelId, EffortLevel effort, EstimateBasis expected)
    {
        CostEstimate estimate = _estimator.Estimate(AnswerSettings.Create(modelId, effort), CorpusTokens);

        Assert.Equal(expected, estimate.Basis);
        Assert.Equal(expected == EstimateBasis.Measured, estimate.IsMeasured);
    }

    [Fact]
    public void WithNoMeasurements_NothingIsMeasured()
    {
        _options.MeasuredOutputTokens.Clear();

        IEnumerable<AnswerSettings> combinations = CacheAffecting.SelectableModels
            .SelectMany(model => CacheAffecting.EffortLevels.Select(level => AnswerSettings.Create(model.Id, level)));
        Assert.All(combinations, settings => Assert.NotEqual(EstimateBasis.Measured, _estimator.BasisOf(settings)));
    }

    [Fact]
    public void ACorpus_IsEstimatedAtItsCountOnTheSelectedModel()
    {
        var corpus = new DocQaMs.Core.Corpus.Corpus(
            TestCorpus.Create().Documents,
            "fingerprint",
            new Dictionary<string, long> { [ModelIds.ClaudeOpus5] = 100_000, [ModelIds.ClaudeSonnet5] = 150_000 });
        AnswerSettings sonnet = AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Low);

        Assert.Equal(_estimator.Estimate(sonnet, 150_000), _estimator.Estimate(sonnet, corpus));
    }
}
