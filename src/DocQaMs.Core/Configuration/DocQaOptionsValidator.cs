using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Configuration;

/// <summary>
/// Checks <see cref="DocQaOptions"/> against the models and levels in <see cref="CacheAffecting"/>.
/// </summary>
/// <remarks>
/// Runs at startup, so a gap in the configuration fails there instead of surfacing later as a
/// <see cref="KeyNotFoundException"/>, or as a $0 cost in the selector's preview.
/// </remarks>
public sealed class DocQaOptionsValidator : IValidateOptions<DocQaOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, DocQaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        const string section = DocQaOptions.SectionName;

        // A null in the configuration file can bind as a null dictionary or a null entry; both are
        // reported as missing rather than allowed to throw here.
        Dictionary<string, ModelPrices> pricesByModel = options.Prices ?? [];
        Dictionary<EffortLevel, int> maxTokensByLevel = options.MaxTokens ?? [];
        Dictionary<EffortLevel, TimeSpan> timeoutsByLevel = options.RequestTimeouts ?? [];
        Dictionary<EffortLevel, OutputTokenRange> rangesByLevel = options.EstimatedOutputTokens ?? [];

        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            if (!pricesByModel.TryGetValue(model.Id, out ModelPrices? prices) || prices is null)
            {
                failures.Add($"{section}:Prices has no row for the selectable model '{model.Id}'.");
                continue;
            }

            foreach ((string bucket, decimal price) in Buckets(prices))
            {
                if (price <= 0)
                {
                    failures.Add($"{section}:Prices:{model.Id}:{bucket} must be greater than zero, but is {price}.");
                }
            }
        }

        foreach (EffortLevel level in CacheAffecting.EffortLevels)
        {
            if (!maxTokensByLevel.TryGetValue(level, out int maxTokens))
            {
                failures.Add($"{section}:MaxTokens has no value for the effort level {level}.");
            }
            else if (maxTokens is < 1 or > DocQaOptions.ModelOutputTokenLimit)
            {
                failures.Add(
                    $"{section}:MaxTokens:{level} must be between 1 and {DocQaOptions.ModelOutputTokenLimit:N0}, " +
                    $"the models' output limit, but is {maxTokens:N0}.");
            }

            if (!timeoutsByLevel.TryGetValue(level, out TimeSpan timeout))
            {
                failures.Add($"{section}:RequestTimeouts has no value for the effort level {level}.");
            }
            else if (timeout <= TimeSpan.Zero)
            {
                failures.Add($"{section}:RequestTimeouts:{level} must be positive, but is {timeout}.");
            }

            if (!rangesByLevel.TryGetValue(level, out OutputTokenRange? range) || range is null)
            {
                failures.Add($"{section}:EstimatedOutputTokens has no range for the effort level {level}.");
            }
            else if (range.Lowest < 1 || range.Lowest > range.Highest)
            {
                failures.Add(
                    $"{section}:EstimatedOutputTokens:{level} must have 1 <= Lowest <= Highest, " +
                    $"but is {range.Lowest:N0} to {range.Highest:N0}.");
            }
        }

        foreach ((string modelId, Dictionary<EffortLevel, OutputTokenRange>? measuredByLevel) in options.MeasuredOutputTokens ?? [])
        {
            // A measurement of a model that cannot be selected is a typo or a leftover, never used.
            if (!CacheAffecting.SelectableModels.Any(model => model.Id == modelId))
            {
                failures.Add($"{section}:MeasuredOutputTokens names '{modelId}', which is not a selectable model.");
                continue;
            }

            foreach ((EffortLevel level, OutputTokenRange? range) in measuredByLevel ?? [])
            {
                if (range is null || range.Lowest < 1 || range.Lowest > range.Highest)
                {
                    failures.Add(
                        $"{section}:MeasuredOutputTokens:{modelId}:{level} must have 1 <= Lowest <= Highest, " +
                        (range is null ? "but is missing." : $"but is {range.Lowest:N0} to {range.Highest:N0}."));
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static IEnumerable<(string Bucket, decimal Price)> Buckets(ModelPrices prices) =>
    [
        (nameof(ModelPrices.InputPerMTok), prices.InputPerMTok),
        (nameof(ModelPrices.CacheWrite5mPerMTok), prices.CacheWrite5mPerMTok),
        (nameof(ModelPrices.CacheWrite1hPerMTok), prices.CacheWrite1hPerMTok),
        (nameof(ModelPrices.CacheReadPerMTok), prices.CacheReadPerMTok),
        (nameof(ModelPrices.OutputPerMTok), prices.OutputPerMTok),
    ];
}
