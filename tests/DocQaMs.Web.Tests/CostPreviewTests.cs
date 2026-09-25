using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Web.Tests.TestSupport;

namespace DocQaMs.Web.Tests;

/// <summary>
/// The preview's wording. The figures are the estimator's (Core's <c>CostEstimatorTests</c> recompute them from the
/// price table); these check only that each lands in the right sentence, with the right basis and caution.
/// </summary>
public sealed class CostPreviewTests
{
    private static readonly CacheWarmth Cold = new(CacheWarmthStatus.Cold, null);

    // The only measured combination, and the one the Estimate fixture's figures belong to.
    private static readonly AnswerSettings OpusHigh = AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.High);

    [Fact]
    public void ACold_MeasuredCombination_GivesTheFirstQuestionThenEachAfter_AndTheWorstCase()
    {
        CostPreviewText preview = CostPreview.Describe(Estimate(OpusHigh), Cold, TimeZoneInfo.Utc, followUp: false);

        Assert.Equal("Opus 5, High: cold, so $2.08–$2.16 for the first question (measured), then about $0.18–$0.30 each.", preview.Summary);
        Assert.Equal("Up to $2.80 if an answer runs to its output limit.", preview.WorstCase);
        Assert.Null(preview.Caution);
        Assert.Null(preview.Warning);
    }

    [Fact]
    public void AColdEntry_InAConversationWithAnswers_PricesTheNextQuestion()
    {
        CostPreviewText preview = CostPreview.Describe(Estimate(OpusHigh), Cold, TimeZoneInfo.Utc, followUp: true);

        Assert.Contains("for the next question", preview.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbablyWarmEntry_SaysUntilWhen_InTheViewersZone()
    {
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
        var warm = new CacheWarmth(CacheWarmthStatus.ProbablyWarm, new DateTimeOffset(2026, 9, 14, 19, 27, 0, TimeSpan.Zero));

        CostPreviewText preview = CostPreview.Describe(Estimate(TestData.Sonnet(EffortLevel.Low)), warm, zone, followUp: false);

        Assert.Equal("Sonnet 5, Low: probably warm until 14:27, so about $0.18–$0.30 per question (estimated).", preview.Summary);
        Assert.Equal("Up to $2.80 if an answer runs to its output limit.", preview.WorstCase);
    }

    [Theory]
    [InlineData(EffortLevel.Low, true)]
    [InlineData(EffortLevel.Medium, true)]
    [InlineData(EffortLevel.High, false)]
    [InlineData(EffortLevel.XHigh, false)]
    [InlineData(EffortLevel.Max, false)]
    public void OnlyLevelsBelowHigh_CarryTheCaution(EffortLevel effort, bool cautioned)
    {
        CostPreviewText preview = CostPreview.Describe(Estimate(TestData.Sonnet(effort)), Cold, TimeZoneInfo.Utc, followUp: false);

        Assert.Equal(cautioned ? CostPreview.LowLevelCaution : null, preview.Caution);
    }

    [Theory]
    [InlineData(EstimateBasis.Estimate)]
    [InlineData(EstimateBasis.Assumption)]
    public void EveryUnmeasuredFigure_SaysEstimated(EstimateBasis basis)
    {
        CostPreviewText preview = CostPreview.Describe(Estimate(OpusHigh) with { Basis = basis }, Cold, TimeZoneInfo.Utc, followUp: false);

        Assert.Contains("(estimated)", preview.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("measured", preview.Summary.Replace("(estimated)", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AColdWriteInProgressElsewhere_WarnsOfASecondWrite()
    {
        var warmth = new CacheWarmth(CacheWarmthStatus.ColdWriteInProgress, null);

        CostPreviewText preview = CostPreview.Describe(Estimate(OpusHigh), warmth, TimeZoneInfo.Utc, followUp: false);

        Assert.Equal(
            "A cold write is in progress in another conversation. Asking now may pay a second $2.00 write; wait until that answer starts streaming.",
            preview.Warning);
        Assert.Contains("cold, so", preview.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARangeWhoseEndsRoundToTheSameCent_IsOneAmount() =>
        Assert.Equal("$0.10", Formatting.Money(new CostRange(0.0999m, 0.1049m)));

    // The app's own estimator, for each combination's basis as configured.
    private static readonly CostEstimator Estimator = new(Microsoft.Extensions.Options.Options.Create(TestData.Options()));

    // The figures of Opus 5 at High in the Cost Reference, as the estimator computes them.
    private static CostEstimate Estimate(AnswerSettings settings) => new(
        settings,
        Estimator.BasisOf(settings),
        ColdInput: 1.99745m,
        WarmInput: 0.0998725m,
        new CostRange(2.07745m, 2.15745m),
        new CostRange(0.1798725m, 0.2998725m),
        WorstCaseCold: 2.79745m,
        WorstCaseFollowUp: 1.0998725m,
        OutputLimit: 32_000);
}
