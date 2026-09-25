namespace DocQaMs.Core.Quotes;

/// <summary>
/// The outcome of checking one distinct cited span against the text that was sent.
/// </summary>
/// <remarks>
/// Several outcomes rather than pass or fail, so that offsets pointing outside the text held, which would be
/// a bug in what is compared against, are never mistaken for a quote that fails to match its source.
/// </remarks>
public enum MatchStatus
{
    /// <summary>The source text at the cited span is exactly the quoted text.</summary>
    Matched,

    /// <summary>In range and long enough, but the source text there is not the quote.</summary>
    Mismatch,

    /// <summary>The document index or the span falls outside the text that was sent.</summary>
    OutOfRange,

    /// <summary>Empty, or too short to mean anything; it would match trivially.</summary>
    Degenerate,

    /// <summary>Not a readable <c>char_location</c> citation, so there are no offsets to compare.</summary>
    NotCheckable,
}

/// <summary>
/// Conversions for <see cref="MatchStatus"/>.
/// </summary>
public static class MatchStatusExtensions
{
    /// <summary>
    /// Returns the status as the UI shows it, in the prototype's words. None of them is "verified": a match
    /// shows where a quote came from, not that it supports the claim beside it.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>One of <c>matched</c>, <c>mismatch</c>, <c>out of range</c>, <c>degenerate</c> or <c>not checkable</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not a defined value.</exception>
    public static string ToDisplayName(this MatchStatus status) => status switch
    {
        MatchStatus.Matched => "matched",
        MatchStatus.Mismatch => "mismatch",
        MatchStatus.OutOfRange => "out of range",
        MatchStatus.Degenerate => "degenerate",
        MatchStatus.NotCheckable => "not checkable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a defined match status."),
    };
}
