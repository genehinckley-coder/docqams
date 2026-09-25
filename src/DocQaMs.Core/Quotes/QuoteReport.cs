namespace DocQaMs.Core.Quotes;

/// <summary>
/// Every cited span of one answer checked, and how much of the answer is sourced.
/// </summary>
/// <remarks>Characters are code points that are not whitespace.</remarks>
/// <param name="Checks">One per distinct span. <c>Checks[n - 1]</c> is source <c>[n]</c>.</param>
/// <param name="CitationCount">Citations across every block, repeats of the same span included.</param>
/// <param name="CitedBlocks">Every text block that cites something and has visible text, in order.</param>
/// <param name="AnswerCharacters">Non-whitespace characters across the answer's text blocks.</param>
/// <param name="UncitedCharacters">Of those, the characters in blocks with no citation.</param>
public sealed record QuoteReport(
    IReadOnlyList<QuoteCheck> Checks,
    int CitationCount,
    IReadOnlyList<BlockCoverage> CitedBlocks,
    int AnswerCharacters,
    int UncitedCharacters)
{
    /// <summary>The number of spans that matched.</summary>
    public int MatchedCount => Checks.Count(check => check.IsMatched);

    /// <summary>Every span that did not match, with its source number.</summary>
    public IReadOnlyList<(int SourceNumber, QuoteCheck Check)> Failures =>
        [.. Checks.Select((check, i) => (i + 1, check)).Where(failure => !failure.check.IsMatched)];

    /// <summary>The cited blocks that are thinly sourced.</summary>
    public IReadOnlyList<BlockCoverage> ThinBlocks => [.. CitedBlocks.Where(block => block.IsThin)];

    /// <summary>The share of the answer's characters in blocks with no citation; 0 for an empty answer.</summary>
    public double UncitedFraction => AnswerCharacters == 0 ? 0.0 : (double)UncitedCharacters / AnswerCharacters;
}
