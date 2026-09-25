using System.ComponentModel.DataAnnotations;

namespace DocQaMs.Core.Configuration;

/// <summary>
/// Settings bound from the <c>DocQaMs</c> configuration section.
/// </summary>
/// <remarks>
/// Nothing here is part of the cached request prefix; values that are live in
/// <see cref="CacheAffecting"/> instead. Relative paths resolve against the web app's content root.
/// Cross-checks against the selectable models and levels are in <see cref="DocQaOptionsValidator"/>.
/// </remarks>
public sealed class DocQaOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "DocQaMs";

    /// <summary>The output ceiling of both selectable models; no level's <see cref="MaxTokens"/> may exceed it.</summary>
    public const int ModelOutputTokenLimit = 128_000;

    /// <summary>Folder holding the corpus pages. Only its top level is read.</summary>
    [Required]
    public string CorpusDirectory { get; set; } = "";

    /// <summary>The API key file. Deliberately outside the repository.</summary>
    [Required]
    public string ApiKeyPath { get; set; } = "";

    /// <summary>The corpus state file, which records the fingerprint from the previous run.</summary>
    [Required]
    public string StateFilePath { get; set; } = "";

    /// <summary>Largest corpus, in tokens, the app will send. Above it the corpus is refused, never truncated.</summary>
    [Range(1, int.MaxValue)]
    public int TokenCeiling { get; set; }

    /// <summary>The models' context window in tokens, used for the conversation-growth warning.</summary>
    [Range(1, int.MaxValue)]
    public int ContextWindow { get; set; }

    /// <summary>Fraction of <see cref="ContextWindow"/> at which a conversation is warned.</summary>
    [Range(0.01, 1.0)]
    public double ContextWarnFraction { get; set; }

    /// <summary>How far a turn's cache read may fall short of the expected corpus prefix before it is called a miss.</summary>
    [Range(0.0, 0.5)]
    public double CacheReadToleranceFraction { get; set; }

    /// <summary>Citation spans with fewer non-whitespace characters than this are rejected as degenerate.</summary>
    [Range(1, int.MaxValue)]
    public int MinCitationLength { get; set; }

    /// <summary>A cited answer block whose matched source text is shorter than this fraction of it is thinly sourced.</summary>
    [Range(0.01, 1.0)]
    public double ThinCoverageFraction { get; set; }

    /// <summary>The request's <c>max_tokens</c> for each effort level. Thinking counts toward it.</summary>
    public Dictionary<EffortLevel, int> MaxTokens { get; set; } = [];

    /// <summary>
    /// The SDK timeout for each effort level. It covers the whole HTTP call, including reading the
    /// streamed answer, so it must exceed the time an answer at that level's output limit takes.
    /// </summary>
    public Dictionary<EffortLevel, TimeSpan> RequestTimeouts { get; set; } = [];

    /// <summary>Prices in USD per million tokens, keyed by model id. For display only, never for behavior.</summary>
    public Dictionary<string, ModelPrices> Prices { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Estimated output tokens (thinking plus answer) per answer at each effort level. Used only for the
    /// model and level selector's cost preview.
    /// </summary>
    public Dictionary<EffortLevel, OutputTokenRange> EstimatedOutputTokens { get; set; } = [];

    /// <summary>
    /// Output ranges measured on particular combinations, keyed by model id and then effort level. Each replaces
    /// <see cref="EstimatedOutputTokens"/> for its own combination, whose cost figures are then labeled measured.
    /// Used only for the cost preview.
    /// </summary>
    public Dictionary<string, Dictionary<EffortLevel, OutputTokenRange>> MeasuredOutputTokens { get; set; } = new(StringComparer.Ordinal);
}
