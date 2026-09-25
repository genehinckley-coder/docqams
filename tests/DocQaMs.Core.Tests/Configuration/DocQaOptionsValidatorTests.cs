using System.Text;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Tests.Configuration;

public sealed class DocQaOptionsValidatorTests
{
    private readonly DocQaOptionsValidator _validator = new();

    [Fact]
    public void CompleteOptions_Pass() =>
        Assert.True(_validator.Validate(null, TestSettings.ValidOptions()).Succeeded);

    [Fact]
    public void TheShippedAppSettings_Pass()
    {
        ValidateOptionsResult result = _validator.Validate(null, LoadShippedOptions());

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void TheShippedAppSettings_HaveThePlansLimitsAndTimeouts()
    {
        DocQaOptions options = LoadShippedOptions();

        // Low through High keep the prototype's 32,000; xhigh and max get room for longer thinking.
        Assert.Equal(32_000, options.MaxTokens[EffortLevel.High]);
        Assert.Equal(64_000, options.MaxTokens[EffortLevel.XHigh]);
        Assert.Equal(128_000, options.MaxTokens[EffortLevel.Max]);
        Assert.Equal(TimeSpan.FromMinutes(20), options.RequestTimeouts[EffortLevel.High]);
        Assert.Equal(TimeSpan.FromMinutes(30), options.RequestTimeouts[EffortLevel.XHigh]);
        Assert.Equal(TimeSpan.FromMinutes(60), options.RequestTimeouts[EffortLevel.Max]);
    }

    [Theory]
    [InlineData(ModelIds.ClaudeOpus5)]
    [InlineData(ModelIds.ClaudeSonnet5)]
    public void AMissingPriceRow_Fails(string modelId)
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.Prices.Remove(modelId);

        AssertFailsMentioning(options, modelId);
    }

    [Fact]
    public void AZeroPrice_Fails()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.Prices[ModelIds.ClaudeSonnet5].CacheReadPerMTok = 0;

        AssertFailsMentioning(options, nameof(ModelPrices.CacheReadPerMTok));
    }

    [Theory]
    [InlineData(EffortLevel.Low)]
    [InlineData(EffortLevel.XHigh)]
    [InlineData(EffortLevel.Max)]
    public void AMissingMaxTokens_Fails(EffortLevel level)
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.MaxTokens.Remove(level);

        AssertFailsMentioning(options, $"MaxTokens has no value for the effort level {level}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DocQaOptions.ModelOutputTokenLimit + 1)]
    public void AMaxTokensOutsideTheModelsLimit_Fails(int maxTokens)
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.MaxTokens[EffortLevel.Max] = maxTokens;

        AssertFailsMentioning(options, "MaxTokens:Max");
    }

    [Fact]
    public void AMaxTokensAtTheModelsLimit_Passes()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.MaxTokens[EffortLevel.Max] = DocQaOptions.ModelOutputTokenLimit;

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void AMissingTimeout_Fails()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.RequestTimeouts.Remove(EffortLevel.Max);

        AssertFailsMentioning(options, "RequestTimeouts has no value for the effort level Max");
    }

    [Fact]
    public void AZeroTimeout_Fails()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.RequestTimeouts[EffortLevel.Low] = TimeSpan.Zero;

        AssertFailsMentioning(options, "RequestTimeouts:Low");
    }

    [Fact]
    public void AMissingOutputRange_Fails()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.EstimatedOutputTokens.Remove(EffortLevel.Medium);

        AssertFailsMentioning(options, "EstimatedOutputTokens has no range for the effort level Medium");
    }

    [Fact]
    public void AnInvertedOutputRange_Fails()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.EstimatedOutputTokens[EffortLevel.High] = new OutputTokenRange { Lowest = 6_400, Highest = 3_200 };

        AssertFailsMentioning(options, "EstimatedOutputTokens:High");
    }

    [Fact]
    public void AMeasuredRange_ForAModelThatIsNotSelectable_Fails()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.MeasuredOutputTokens["claude-opus-9"] = new() { [EffortLevel.High] = new OutputTokenRange { Lowest = 1, Highest = 2 } };

        AssertFailsMentioning(options, "MeasuredOutputTokens names 'claude-opus-9', which is not a selectable model");
    }

    [Fact]
    public void AnInvertedMeasuredRange_Fails()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.MeasuredOutputTokens[ModelIds.ClaudeOpus55] = new() { [EffortLevel.High] = new OutputTokenRange { Lowest = 8_300, Highest = 5_300 } };

        AssertFailsMentioning(options, $"MeasuredOutputTokens:{ModelIds.ClaudeOpus55}:High");
    }

    [Fact]
    public void NullMeasuredEntries_FailOrAreIgnored_InsteadOfThrowing()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.MeasuredOutputTokens[ModelIds.ClaudeOpus5] = null!;
        options.MeasuredOutputTokens[ModelIds.ClaudeOpus55] = new() { [EffortLevel.High] = null! };

        AssertFailsMentioning(options, $"MeasuredOutputTokens:{ModelIds.ClaudeOpus55}:High must have 1 <= Lowest <= Highest, but is missing.");

        options.MeasuredOutputTokens = null!;
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void ANullPriceRow_FailsInsteadOfThrowing()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.Prices[ModelIds.ClaudeOpus5] = null!;

        AssertFailsMentioning(options, $"no row for the selectable model '{ModelIds.ClaudeOpus5}'");
    }

    [Fact]
    public void ANullOutputRange_FailsInsteadOfThrowing()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.EstimatedOutputTokens[EffortLevel.High] = null!;

        AssertFailsMentioning(options, "EstimatedOutputTokens has no range for the effort level High");
    }

    [Fact]
    public void NullDictionaries_FailInsteadOfThrowing()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.Prices = null!;
        options.MaxTokens = null!;
        options.RequestTimeouts = null!;
        options.EstimatedOutputTokens = null!;

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.Equal(
            CacheAffecting.SelectableModels.Count + 3 * CacheAffecting.EffortLevels.Count,
            result.Failures!.Count());
    }

    [Fact]
    public void JsonNullEntries_FailValidation()
    {
        Dictionary<string, string?> settings = TestSettings.Valid();
        foreach (string key in settings.Keys.Where(key =>
            key.StartsWith($"DocQaMs:Prices:{ModelIds.ClaudeOpus5}:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DocQaMs:EstimatedOutputTokens:High:", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            settings.Remove(key);
        }

        const string json = """{ "DocQaMs": { "Prices": { "claude-opus-5": null }, "EstimatedOutputTokens": { "High": null } } }""";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        ValidateOptionsResult result = _validator.Validate(null, TestSettings.Bind(configuration));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(ModelIds.ClaudeOpus5, StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("High", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryGapIsReported_NotJustTheFirst()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.Prices.Clear();
        options.MaxTokens.Clear();

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.Equal(
            CacheAffecting.SelectableModels.Count + CacheAffecting.EffortLevels.Count,
            result.Failures!.Count());
    }

    private void AssertFailsMentioning(DocQaOptions options, string expected)
    {
        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(expected, StringComparison.Ordinal));
    }

    private static DocQaOptions LoadShippedOptions() => TestSettings.Bind(
        new ConfigurationBuilder()
            .AddJsonFile(RepoPaths.Combine("src", "DocQaMs.Web", "appsettings.json"), optional: false, reloadOnChange: false)
            .Build());
}
