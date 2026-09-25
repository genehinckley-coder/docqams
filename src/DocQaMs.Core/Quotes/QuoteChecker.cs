using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Quotes;

/// <summary>
/// Checks each quoted passage of an answer, character for character, against the text that was sent. No API
/// call: a <c>char_location</c> citation carries the quote and its offsets, so the check is a local string
/// comparison.
/// </summary>
/// <remarks>
/// <para>
/// A match shows provenance: the quote appears at the cited location. It does not show that the quote
/// supports the claim beside it (see <see cref="MatchDisclaimer"/>), so a match is called "matched" and never
/// anything stronger.
/// </para>
/// <para>
/// Offsets are code points, as the API sends them. Bounds are checked in code points before any conversion,
/// and every span is converted through the document's <see cref="CodePointIndex"/> before slicing. Every
/// character count and position this reports is in code points, as the prototype's are.
/// </para>
/// <para>
/// Nothing here throws on a failed quote: failures are the output. The checker is independent of model and
/// effort level.
/// </para>
/// </remarks>
public sealed class QuoteChecker
{
    /// <summary>
    /// Shown once per conversation beside the quote report: what a match does and does not show. Copied
    /// verbatim from the prototype.
    /// </summary>
    public const string MatchDisclaimer =
        "Quote matching confirms a quoted passage appears at the cited location in the source file. It does " +
        "NOT confirm the quote supports the claim attached to it. Read the cited text before relying on any " +
        "conclusion drawn from it.";

    internal const string CharLocation = AnswerCitation.CharLocation;

    private readonly int _minCitationLength;
    private readonly double _thinCoverageFraction;

    /// <summary>Creates a checker with the configured degenerate and thin-coverage thresholds.</summary>
    /// <param name="options">The options holding <see cref="DocQaOptions.MinCitationLength"/> and <see cref="DocQaOptions.ThinCoverageFraction"/>.</param>
    public QuoteChecker(IOptions<DocQaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _minCitationLength = options.Value.MinCitationLength;
        _thinCoverageFraction = options.Value.ThinCoverageFraction;
    }

    /// <summary>
    /// Compares one citation's quote with the source text at its span.
    /// </summary>
    /// <param name="citation">A citation from the answer.</param>
    /// <param name="corpus">
    /// The snapshot the answer's request was built from, so that <c>document_index</c> indexes the documents
    /// exactly as they were sent. Never the current corpus after a reload.
    /// </param>
    /// <returns>The outcome, with a detail when the quote did not match.</returns>
    public QuoteCheck Check(AnswerCitation citation, CorpusSnapshot corpus)
    {
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(corpus);

        long index = citation.DocumentIndex;
        long start = citation.StartCharIndex;
        long end = citation.EndCharIndex;
        IReadOnlyList<CorpusDocument> documents = corpus.Documents;

        // Validate before slicing, on the raw values: Substring would throw where Python's slicing silently
        // clipped, and a clipped span could even match. Both would be wrong answers.
        if (index < 0 || index >= documents.Count)
        {
            return QuoteCheck.For(citation, MatchStatus.OutOfRange, FormattableString.Invariant(
                $"document {index} does not exist; the corpus has {documents.Count} (numbered from 0)"));
        }

        CorpusDocument document = documents[(int)index];
        if (start > end)
        {
            return QuoteCheck.For(citation, MatchStatus.OutOfRange, FormattableString.Invariant(
                $"the span ends (character {end:N0}) before it starts ({start:N0})"));
        }

        // In code points, not UTF-16 units, or the boundary moves once the text holds a non-BMP character.
        if (start < 0 || end > document.CharacterCount)
        {
            return QuoteCheck.For(citation, MatchStatus.OutOfRange, FormattableString.Invariant(
                $"characters {start:N0}-{end:N0} fall outside document {index}, which has {document.CharacterCount:N0}"));
        }

        string source = Slice(document, start, end);
        int visible = VisibleLength(source);
        if (visible < _minCitationLength)
        {
            string reason = start == end
                ? "the span is empty"
                : FormattableString.Invariant(
                    $"the span has {visible} non-whitespace characters, under the minimum citation length ({_minCitationLength})");
            return QuoteCheck.For(citation, MatchStatus.Degenerate, reason);
        }

        return string.Equals(source, citation.CitedText, StringComparison.Ordinal)
            ? QuoteCheck.For(citation, MatchStatus.Matched)
            : QuoteCheck.For(citation, MatchStatus.Mismatch, DescribeMismatch(source, citation.CitedText, document, (int)start));
    }

    /// <summary>
    /// Checks every distinct cited span of a completed answer and measures how much of it is sourced.
    /// </summary>
    /// <param name="result">The completed answer. Its sources are numbered as its <c>[n]</c> markers are.</param>
    /// <param name="corpus">The snapshot the answer's request was built from.</param>
    /// <returns>The report. <c>Checks[n - 1]</c> is <c>result.Sources[n - 1]</c>.</returns>
    public QuoteReport BuildReport(AnswerResult result, CorpusSnapshot corpus)
    {
        ArgumentNullException.ThrowIfNull(result);
        return BuildReport(result.Sources, result.Reply.Contents.AsReadOnly(), corpus);
    }

    /// <summary>
    /// Checks every distinct cited span and measures how much of the answer is sourced.
    /// </summary>
    /// <param name="sources">Distinct citations in source-number order, as numbered in the rendered answer.</param>
    /// <param name="content">
    /// The reply's contents, one per content block. Only <see cref="TextContent"/>s are read, with their
    /// <see cref="CitationAnnotation"/>s.
    /// </param>
    /// <param name="corpus">The snapshot the answer's request was built from.</param>
    /// <returns>
    /// The report. A citation in <paramref name="content"/> that is missing from <paramref name="sources"/> is
    /// numbered after them; normally there is none. A citation that cannot be read as <c>char_location</c> is
    /// reported as <see cref="MatchStatus.NotCheckable"/>, not thrown.
    /// </returns>
    public QuoteReport BuildReport(
        IReadOnlyList<AnswerCitation> sources, IReadOnlyList<AIContent> content, CorpusSnapshot corpus)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(corpus);

        var numbering = new SpanNumbering();
        foreach (AnswerCitation source in sources)
        {
            numbering.Number(CitedSpan.Of(source));
        }

        int citationCount = 0;
        int answerCharacters = 0;
        int uncitedCharacters = 0;
        List<(string Text, IReadOnlyList<int> Numbers, int Characters)> cited = [];
        foreach (AIContent block in content)
        {
            if (block is not TextContent text)
            {
                continue;
            }

            List<CitationAnnotation> citations = [.. (text.Annotations ?? []).OfType<CitationAnnotation>()];
            int characters = VisibleLength(text.Text);
            answerCharacters += characters;
            citationCount += citations.Count;

            // Every citation is numbered, and so checked, even in a block too empty to measure.
            List<int> numbers = [];
            foreach (CitationAnnotation citation in citations)
            {
                int number = numbering.Number(CitedSpan.Read(citation));
                if (!numbers.Contains(number))
                {
                    numbers.Add(number);
                }
            }

            if (citations.Count == 0)
            {
                uncitedCharacters += characters;
            }
            else if (characters > 0)
            {
                cited.Add((text.Text, numbers.AsReadOnly(), characters));
            }
        }

        QuoteCheck[] checks = [.. numbering.Spans.Select(span => span.Citation is { } citation
            ? Check(citation, corpus)
            : new QuoteCheck(null, span.Type, span.CitedText, MatchStatus.NotCheckable, span.Unreadable))];
        BlockCoverage[] blocks = [.. cited.Select(block => new BlockCoverage(
            block.Text,
            block.Numbers,
            block.Characters,
            MatchedCharacters(block.Numbers.Select(number => checks[number - 1]), corpus),
            _thinCoverageFraction))];

        return new QuoteReport(checks.AsReadOnly(), citationCount, blocks.AsReadOnly(), answerCharacters, uncitedCharacters);
    }

    /// <summary>Where the quote departs from the span, and whether it appears verbatim elsewhere.</summary>
    private static string DescribeMismatch(string source, string quote, CorpusDocument document, int start)
    {
        List<string> parts =
        [
            FormattableString.Invariant($"the quote departs from the source at character {DifferAt(source, quote):N0} of the span"),
        ];

        int sourceLength = CodePointLength(source);
        int quoteLength = CodePointLength(quote);
        if (quoteLength != sourceLength)
        {
            parts.Add(FormattableString.Invariant($"the span is {sourceLength:N0} characters and the quote {quoteLength:N0}"));
        }

        // Tells an offset error apart from a quote that is not in the document at all.
        int at = quote.Length == 0 ? -1 : FindVerbatim(document, quote);
        if (at >= 0)
        {
            parts.Add(FormattableString.Invariant(
                $"the quote does appear verbatim at character {at:N0} ({at - start:+#,0;-#,0;+0} from the cited start)"));
        }
        else
        {
            parts.Add("the quote appears nowhere verbatim in that document");
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// The code-point position of the first occurrence of <paramref name="quote"/> in the document, or -1.
    /// </summary>
    /// <remarks>
    /// An occurrence must start and end on code-point boundaries, as it must for Python's <c>str.find</c>. A
    /// UTF-16 search alone would let a quote that starts or ends with a lone surrogate match half of a pair, and
    /// report a position where the quote does not occur.
    /// </remarks>
    private static int FindVerbatim(CorpusDocument document, string quote)
    {
        string text = document.Text;
        int found = text.IndexOf(quote, StringComparison.Ordinal);
        while (found >= 0)
        {
            if (IsCodePointBoundary(text, found) && IsCodePointBoundary(text, found + quote.Length))
            {
                return document.CodePoints.ToCodePoint(found);
            }

            found = found + 1 < text.Length ? text.IndexOf(quote, found + 1, StringComparison.Ordinal) : -1;
        }

        return -1;
    }

    /// <summary>Whether a UTF-16 offset falls between two code points rather than inside a surrogate pair.</summary>
    private static bool IsCodePointBoundary(string text, int offset) =>
        offset == 0 || offset == text.Length || !char.IsSurrogatePair(text[offset - 1], text[offset]);

    /// <summary>
    /// Non-whitespace characters under the matched spans, overlapping spans merged per document. Only matched
    /// spans count: a span that fails its check sources nothing. Whitespace is left out, so the blank separator
    /// lines a span often absorbs do not count as sourced text.
    /// </summary>
    private static int MatchedCharacters(IEnumerable<QuoteCheck> checks, CorpusSnapshot corpus)
    {
        int total = 0;
        IEnumerable<IGrouping<long, AnswerCitation>> byDocument = checks
            .Where(check => check.IsMatched && check.Citation is not null)
            .Select(check => check.Citation!)
            .GroupBy(citation => citation.DocumentIndex);

        foreach (IGrouping<long, AnswerCitation> spans in byDocument)
        {
            // A matched span is in range, so its document exists and its offsets fit in an int.
            CorpusDocument document = corpus.Documents[(int)spans.Key];
            long mergedStart = 0;
            long mergedEnd = 0;
            bool open = false;
            foreach (AnswerCitation span in spans.OrderBy(span => span.StartCharIndex).ThenBy(span => span.EndCharIndex))
            {
                if (open && span.StartCharIndex <= mergedEnd)
                {
                    mergedEnd = Math.Max(mergedEnd, span.EndCharIndex);
                    continue;
                }

                if (open)
                {
                    total += VisibleLength(Slice(document, mergedStart, mergedEnd));
                }

                (mergedStart, mergedEnd, open) = (span.StartCharIndex, span.EndCharIndex, true);
            }

            if (open)
            {
                total += VisibleLength(Slice(document, mergedStart, mergedEnd));
            }
        }

        return total;
    }

    /// <summary>Slices a span given in code points, which the caller has already bounds-checked.</summary>
    private static string Slice(CorpusDocument document, long start, long end) =>
        document.Text[document.CodePoints.ToUtf16((int)start)..document.CodePoints.ToUtf16((int)end)];

    /// <summary>The first code-point position at which the two strings differ, or the shorter's length.</summary>
    private static int DifferAt(string source, string quote)
    {
        int position = 0;
        int i = 0;
        int j = 0;
        while (i < source.Length && j < quote.Length)
        {
            if (NextCodePoint(source, ref i) != NextCodePoint(quote, ref j))
            {
                return position;
            }

            position++;
        }

        return position;
    }

    private static int CodePointLength(string text)
    {
        int length = 0;
        for (int i = 0; i < text.Length; length++)
        {
            NextCodePoint(text, ref i);
        }

        return length;
    }

    /// <summary>Code points in <paramref name="text"/> that are not whitespace, as Python defines whitespace.</summary>
    private static int VisibleLength(string text)
    {
        int visible = 0;
        for (int i = 0; i < text.Length;)
        {
            // Every Python whitespace character is in the BMP, so a surrogate pair is never whitespace.
            bool whiteSpace = PythonWhitespace.IsWhiteSpace(text[i]);
            NextCodePoint(text, ref i);
            if (!whiteSpace)
            {
                visible++;
            }
        }

        return visible;
    }

    /// <summary>
    /// Reads the code point at <paramref name="i"/> and advances past it. A well-formed surrogate pair is one
    /// code point; a lone surrogate is one too, as in Python and <see cref="CodePointIndex"/>.
    /// </summary>
    private static int NextCodePoint(string text, ref int i)
    {
        if (char.IsSurrogatePair(text, i))
        {
            int codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
            i += 2;
            return codePoint;
        }

        return text[i++];
    }

    /// <summary>One distinct cited span, readable or not, and what identifies it.</summary>
    /// <param name="Key">What makes two citations the same source span.</param>
    /// <param name="Citation">The citation, or <see langword="null"/> when it could not be read.</param>
    /// <param name="Type">The citation's <c>type</c>.</param>
    /// <param name="CitedText">Its quoted text, or an empty string when it had none.</param>
    /// <param name="Unreadable">Why it could not be read, or <see langword="null"/> when it was.</param>
    private sealed record CitedSpan(object Key, AnswerCitation? Citation, string Type, string CitedText, string? Unreadable)
    {
        public static CitedSpan Of(AnswerCitation citation) =>
            new(citation.SpanKey, citation, CharLocation, citation.CitedText, null);

        /// <summary>Reads a citation from a text content, without throwing on one of an unexpected kind.</summary>
        public static CitedSpan Read(CitationAnnotation citation)
        {
            string type = AnswerCitation.TypeOf(citation);
            if (type == CharLocation && AnswerCitation.TryFrom(citation) is { } location)
            {
                return Of(location);
            }

            // Plain-text documents are cited by char_location only, so this is not expected; such a citation is
            // keyed on its quote, as the prototype keys one.
            string citedText = citation.Snippet ?? "";
            string reason = type == CharLocation
                ? "the char_location citation is missing its quoted text or an offset"
                : $"a \"{type}\" citation carries no character offsets";
            return new CitedSpan((type, citedText), null, type, citedText, reason);
        }
    }

    /// <summary>Numbers distinct spans in the order first seen.</summary>
    private sealed class SpanNumbering
    {
        private readonly Dictionary<object, int> _numbers = [];
        private readonly List<CitedSpan> _spans = [];

        public IReadOnlyList<CitedSpan> Spans => _spans;

        public int Number(CitedSpan span)
        {
            if (!_numbers.TryGetValue(span.Key, out int number))
            {
                _spans.Add(span);
                number = _spans.Count;
                _numbers[span.Key] = number;
            }

            return number;
        }
    }
}
