using System.Text.RegularExpressions;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Text;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// One document of a <see cref="Corpus"/> snapshot: its text exactly as it is sent, and what the quote
/// checker and the document viewer need to work against that text.
/// </summary>
public sealed partial class CorpusDocument
{
    // The extractor separates paragraphs with exactly one blank line, and a paragraph never contains one.
    private const string ParagraphSeparator = "\n\n";

    private const string SectionPrefix = "Section";

    internal CorpusDocument(string fileName, RequestDocument request, long? approximateTokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(request);

        FileName = fileName;
        Request = request;
        CodePoints = CodePointIndex.Build(request.Text);
        Paragraphs = FindParagraphs(request.Text);
        Sections = FindSections(request.Text, Paragraphs);
        ApproximateTokens = approximateTokens;
    }

    /// <summary>The corpus file name, such as <c>ADMINISTRATIVE CODE 41 175.htm</c>.</summary>
    public string FileName { get; }

    /// <summary>The document exactly as it is rendered into a request's document block.</summary>
    public RequestDocument Request { get; }

    /// <summary>The citation label: the document block's <c>title</c>.</summary>
    public string Label => Request.Title;

    /// <summary>The document text. Citation offsets index into exactly this string, in code points.</summary>
    public string Text => Request.Text;

    /// <summary>Converts the code-point offsets citations use into offsets into <see cref="Text"/>.</summary>
    public CodePointIndex CodePoints { get; }

    /// <summary>The text's length in code points, the unit citations use and every displayed count uses.</summary>
    public int CharacterCount => CodePoints.Length;

    /// <summary>
    /// Each paragraph's range in <see cref="Text"/>, in UTF-16 offsets and in order. The blank line between
    /// two paragraphs belongs to neither.
    /// </summary>
    public IReadOnlyList<TextRange> Paragraphs { get; }

    /// <summary>
    /// This document's tokens when counted on its own, or <see langword="null"/> when the count failed.
    /// Approximate: documents counted separately do not sum to the corpus total. For display only.
    /// </summary>
    public long? ApproximateTokens { get; }

    /// <summary>
    /// Each section's heading in the body, in order: the paragraphs that start with <c>Section NNN.NNN</c>. A
    /// document lists its headings in a table of contents before the body repeats them, so only each section's
    /// last heading is kept.
    /// </summary>
    public IReadOnlyList<SectionHeading> Sections { get; }

    /// <summary>
    /// Finds the paragraphs a cited span covers, and the part of each it covers. A span that crosses the blank
    /// line between two paragraphs gives one piece for each, and the blank line belongs to neither.
    /// </summary>
    /// <param name="start">The span's start, in code points, inclusive, as a citation gives it.</param>
    /// <param name="end">The span's end, in code points, exclusive.</param>
    /// <returns>
    /// The pieces in order; empty when the span is empty, falls outside the text, or covers only the blank
    /// line between two paragraphs.
    /// </returns>
    public IReadOnlyList<ParagraphSpan> FindHighlight(long start, long end)
    {
        // Checked in code points, before conversion, as the quote checker checks them.
        if (start < 0 || end > CodePoints.Length || start >= end)
        {
            return [];
        }

        int from = CodePoints.ToUtf16((int)start);
        int to = CodePoints.ToUtf16((int)end);

        // The first paragraph that ends after the span starts; the ranges are in order and never overlap.
        int low = 0;
        int high = Paragraphs.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (Paragraphs[middle].End <= from)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        List<ParagraphSpan> spans = [];
        for (int i = low; i < Paragraphs.Count && Paragraphs[i].Start < to; i++)
        {
            TextRange paragraph = Paragraphs[i];
            int pieceStart = Math.Max(from, paragraph.Start) - paragraph.Start;
            int pieceEnd = Math.Min(to, paragraph.End) - paragraph.Start;
            if (pieceEnd > pieceStart)
            {
                spans.Add(new ParagraphSpan(i, new TextRange(pieceStart, pieceEnd)));
            }
        }

        return spans.AsReadOnly();
    }

    internal static IReadOnlyList<SectionHeading> FindSections(string text, IReadOnlyList<TextRange> paragraphs)
    {
        // Keyed by number, not by text: in Part 176 some contents entries are worded differently from the body's
        // heading. The body comes after the contents, so the last heading of each number is the body's.
        Dictionary<string, SectionHeading> last = new(StringComparer.Ordinal);
        for (int i = 0; i < paragraphs.Count; i++)
        {
            TextRange range = paragraphs[i];
            if (!text.AsSpan(range.Start, range.Length).StartsWith(SectionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string heading = text[range.Start..range.End];
            Match match = SectionPattern().Match(heading);
            if (match.Success)
            {
                string number = match.Groups["number"].Value;
                last[number] = new SectionHeading(i, number, heading);
            }
        }

        return last.Values.OrderBy(section => section.Paragraph).ToList().AsReadOnly();
    }

    // "Section 175.410 Sumps" and "Section 175.APPENDIX A UST Activity ...". [0-9], not \d, which matches any
    // Unicode digit.
    [GeneratedRegex(@"^Section\s+(?<number>[0-9]+\.(?:APPENDIX\s+\S+|[0-9A-Za-z]+))(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SectionPattern();

    internal static IReadOnlyList<TextRange> FindParagraphs(string text)
    {
        List<TextRange> ranges = [];
        int start = 0;
        while (true)
        {
            int separator = text.IndexOf(ParagraphSeparator, start, StringComparison.Ordinal);
            int end = separator < 0 ? text.Length : separator;
            if (end > start)
            {
                ranges.Add(new TextRange(start, end));
            }

            if (separator < 0)
            {
                return ranges.AsReadOnly();
            }

            start = separator + ParagraphSeparator.Length;
        }
    }
}
