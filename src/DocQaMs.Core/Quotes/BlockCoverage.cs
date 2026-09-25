namespace DocQaMs.Core.Quotes;

/// <summary>
/// How much matched source text stands behind one cited answer block.
/// </summary>
/// <remarks>
/// A length proxy, not a measurement: citations attach to the whole block, and the block paraphrases what it
/// cites. What it catches is a long block resting on a short quote, where uncited assertions can ride along.
/// Characters are code points that are not whitespace.
/// </remarks>
/// <param name="Text">The block's text.</param>
/// <param name="SourceNumbers">The numbers of the sources this block cites, once each, in order.</param>
/// <param name="BlockCharacters">Non-whitespace characters in the block. Always positive.</param>
/// <param name="CitedCharacters">Non-whitespace characters in the block's matched spans, overlaps counted once.</param>
/// <param name="ThinCoverageFraction">The fraction below which the block is thinly sourced.</param>
public sealed record BlockCoverage(
    string Text,
    IReadOnlyList<int> SourceNumbers,
    int BlockCharacters,
    int CitedCharacters,
    double ThinCoverageFraction)
{
    /// <summary>The matched characters over the block's characters, capped at 1.</summary>
    public double Fraction => Math.Min(1.0, (double)CitedCharacters / BlockCharacters);

    /// <summary>Whether <see cref="Fraction"/> is below <see cref="ThinCoverageFraction"/>. Exactly at it is not thin.</summary>
    public bool IsThin => Fraction < ThinCoverageFraction;
}
