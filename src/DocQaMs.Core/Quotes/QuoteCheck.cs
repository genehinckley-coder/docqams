using DocQaMs.Core.Answering;

namespace DocQaMs.Core.Quotes;

/// <summary>
/// The outcome for one distinct cited span.
/// </summary>
/// <param name="Citation">
/// The citation that was checked, or <see langword="null"/> when it was <see cref="MatchStatus.NotCheckable"/>
/// and could not be read as a <c>char_location</c> citation.
/// </param>
/// <param name="CitationType">The citation's <c>type</c>; <c>char_location</c> for every checkable citation.</param>
/// <param name="CitedText">The text the API reports it quoted, or an empty string when it sent none.</param>
/// <param name="Status">The outcome.</param>
/// <param name="Detail">Why the quote did not match, or <see langword="null"/> when it did.</param>
public sealed record QuoteCheck(
    AnswerCitation? Citation,
    string CitationType,
    string CitedText,
    MatchStatus Status,
    string? Detail)
{
    /// <summary>Whether the source text at the cited span is exactly the quote.</summary>
    public bool IsMatched => Status == MatchStatus.Matched;

    internal static QuoteCheck For(AnswerCitation citation, MatchStatus status, string? detail = null) =>
        new(citation, QuoteChecker.CharLocation, citation.CitedText, status, detail);
}
