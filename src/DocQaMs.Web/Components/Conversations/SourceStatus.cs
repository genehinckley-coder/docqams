using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using DocQaMs.Web.Components.Ds;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Web.Components.Conversations;

/// <summary>How a checked source is labeled, and where its span falls, on the Conversation and Documents screens.</summary>
internal static class SourceStatus
{
    /// <summary>The badge color: success for a match, danger for a mismatch, warning for the other three.</summary>
    /// <param name="status">The source's status.</param>
    /// <returns>The tone. The badge's text carries the status; the color only repeats it.</returns>
    public static BadgeTone Tone(MatchStatus status) => status switch
    {
        MatchStatus.Matched => BadgeTone.Success,
        MatchStatus.Mismatch => BadgeTone.Danger,
        _ => BadgeTone.Warning,
    };

    /// <summary>
    /// The document label and character range, as the prototype shows them. No section number: spans can start
    /// mid-sentence, even in the previous section's source note, so "the nearest Section heading" could mislabel a
    /// legal citation.
    /// </summary>
    /// <param name="check">The source.</param>
    /// <param name="corpus">The corpus snapshot its answer was checked against.</param>
    /// <returns>For example <c>41 Ill. Adm. Code 175: … · characters 36–65</c>.</returns>
    public static string Label(QuoteCheck check, CorpusSnapshot corpus)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(corpus);
        if (check.Citation is not { } citation)
        {
            return $"Not a readable citation ({check.CitationType})";
        }

        IReadOnlyList<CorpusDocument> documents = corpus.Documents;
        string document = citation.DocumentIndex >= 0 && citation.DocumentIndex < documents.Count
            ? documents[(int)citation.DocumentIndex].Label
            : citation.DocumentTitle ?? $"Document {citation.DocumentIndex}";
        return $"{document} · characters {Formatting.Number(citation.StartCharIndex)}–{Formatting.Number(citation.EndCharIndex)}";
    }

    /// <summary>Finds the cited document and the paragraphs the span covers.</summary>
    /// <param name="check">The source.</param>
    /// <param name="corpus">The corpus snapshot its answer was checked against.</param>
    /// <returns>
    /// The document's index and the pieces to highlight, which are empty when the span is out of range, empty, or
    /// covers only a blank line; <see langword="null"/> when there is no readable citation or its document is not
    /// in the snapshot.
    /// </returns>
    public static (int Document, IReadOnlyList<ParagraphSpan> Spans)? Locate(QuoteCheck check, CorpusSnapshot corpus)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(corpus);
        if (check.Citation is not { } citation || citation.DocumentIndex < 0 || citation.DocumentIndex >= corpus.Documents.Count)
        {
            return null;
        }

        int document = (int)citation.DocumentIndex;
        return (document, corpus.Documents[document].FindHighlight(citation.StartCharIndex, citation.EndCharIndex));
    }
}
