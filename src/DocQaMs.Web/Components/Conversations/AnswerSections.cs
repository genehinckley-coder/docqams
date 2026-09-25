using DocQaMs.Core.Conversations;

namespace DocQaMs.Web.Components.Conversations;

/// <summary>
/// An answer split at the <c>Summary</c> and <c>Detail</c> headings the system prompt asks for, so the screen can show
/// the summary first and the cited detail collapsed beneath it.
/// </summary>
/// <param name="Summary">
/// What shows as the answer: the text under the Summary heading, or the whole answer when it has no headings.
/// </param>
/// <param name="Detail">The text under the Detail heading; empty when there is none.</param>
internal sealed record AnswerSections(IReadOnlyList<AnswerSegment> Summary, IReadOnlyList<AnswerSegment> Detail)
{
    private static readonly char[] LineWhitespace = [' ', '\t', '\r', '\n'];

    private enum Section
    {
        Summary,
        Detail,
    }

    /// <summary>Whether the summary shows any text.</summary>
    public bool HasSummaryText => Summary.Any(segment => segment.Text.Length > 0);

    /// <summary>
    /// Splits an answer's segments at its heading lines, which are dropped. A segment the Detail heading falls inside
    /// is cut in two, and its <c>[n]</c> markers stay with the second part, since they follow the block's end. An
    /// answer with neither heading (one asked before the prompt required them, or one where the model ignored it) is
    /// returned whole as the summary, unchanged. So is one whose summary is empty, which would otherwise show nothing
    /// until the detail was opened.
    /// </summary>
    /// <param name="segments">The answer's text blocks, finished or still streaming.</param>
    /// <returns>The summary and the detail.</returns>
    public static AnswerSections Split(IReadOnlyList<AnswerSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        List<AnswerSegment> summary = [];
        List<AnswerSegment> detail = [];
        Section section = Section.Summary;
        bool sawHeading = false;

        // A heading is a whole line, so one is recognized only where a line starts, including across blocks.
        bool atLineStart = true;
        for (int s = 0; s < segments.Count; s++)
        {
            AnswerSegment segment = segments[s];
            string text = segment.Text;
            bool lastSegment = s == segments.Count - 1;
            int pieceStart = 0;
            int lineStart = 0;

            while (lineStart < text.Length)
            {
                int newline = text.IndexOf('\n', lineStart);
                int lineEnd = newline < 0 ? text.Length : newline + 1;

                // A line running on into the next block is not complete here; the last block's last line is.
                bool complete = newline >= 0 || lastSegment;

                // Headings only move forward, so a later "Summary" line (a closing recap, say) stays in the detail.
                if (atLineStart && complete && HeadingOf(text.AsSpan(lineStart, lineEnd - lineStart)) is { } heading
                    && (!sawHeading || heading > section))
                {
                    AddPiece(section == Section.Summary ? summary : detail, text[pieceStart..lineStart], []);
                    section = heading;
                    sawHeading = true;
                    pieceStart = lineEnd;
                }

                atLineStart = newline >= 0;
                lineStart = lineEnd;
            }

            AddPiece(section == Section.Summary ? summary : detail, text[pieceStart..], segment.SourceNumbers);
        }

        if (!sawHeading)
        {
            return new AnswerSections(segments, []);
        }

        Trim(summary);
        Trim(detail);
        return summary.Any(segment => segment.Text.Length > 0) ? new AnswerSections(summary, detail) : new AnswerSections(detail, []);
    }

    // The prompt asks for the bare word alone on its line; a model that adds markdown emphasis, a markdown heading or a
    // colon still gets its answer split.
    private static Section? HeadingOf(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> word = line.Trim().TrimStart('#').Trim().Trim("*_").TrimEnd(':').Trim();
        if (word.Equals("Summary", StringComparison.OrdinalIgnoreCase))
        {
            return Section.Summary;
        }

        return word.Equals("Detail", StringComparison.OrdinalIgnoreCase) || word.Equals("Details", StringComparison.OrdinalIgnoreCase)
            ? Section.Detail
            : null;
    }

    private static void AddPiece(List<AnswerSegment> section, string text, IReadOnlyList<int> sourceNumbers)
    {
        if (text.Length > 0 || sourceNumbers.Count > 0)
        {
            section.Add(new AnswerSegment(text, sourceNumbers));
        }
    }

    // The blank lines around a dropped heading would otherwise show as space above and below each part, since the
    // answer keeps its line breaks.
    private static void Trim(List<AnswerSegment> section)
    {
        while (section.Count > 0)
        {
            AnswerSegment first = section[0];
            string text = first.Text.TrimStart(LineWhitespace);
            if (text.Length > 0 || first.SourceNumbers.Count > 0)
            {
                section[0] = first with { Text = text };
                break;
            }

            section.RemoveAt(0);
        }

        while (section.Count > 0)
        {
            AnswerSegment last = section[^1];
            string text = last.Text.TrimEnd(LineWhitespace);
            if (text.Length > 0 || last.SourceNumbers.Count > 0)
            {
                section[^1] = last with { Text = text };
                break;
            }

            section.RemoveAt(section.Count - 1);
        }
    }
}
