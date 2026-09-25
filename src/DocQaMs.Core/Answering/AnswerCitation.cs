using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>
/// One <c>char_location</c> citation from an answer: a span of one plain-text document as sent.
/// </summary>
/// <remarks>
/// Read from a <see cref="CitationAnnotation"/>: the quote is its <see cref="CitationAnnotation.Snippet"/>,
/// the title its <see cref="CitationAnnotation.Title"/>, and the document index, offsets and citation type
/// are under the <see cref="ChatKeys"/> in its <see cref="AIAnnotation.AdditionalProperties"/>. The offsets
/// are code points, not UTF-16 units; convert them through the document's code-point index before slicing.
/// </remarks>
/// <param name="DocumentIndex">Index into the documents in the order they were sent.</param>
/// <param name="StartCharIndex">Start of the cited span, in code points, inclusive.</param>
/// <param name="EndCharIndex">End of the cited span, in code points, exclusive.</param>
/// <param name="CitedText">The text the API reports it quoted. It is checked, never trusted.</param>
/// <param name="DocumentTitle">The cited document's title, or <see langword="null"/> when the API sent none.</param>
public sealed record AnswerCitation(
    long DocumentIndex,
    long StartCharIndex,
    long EndCharIndex,
    string CitedText,
    string? DocumentTitle)
{
    /// <summary>The citation type of a plain-text document's citations, the only kind this app handles.</summary>
    internal const string CharLocation = "char_location";

    /// <summary>
    /// Identifies the cited span. A span cited more than once is one source, with one number.
    /// </summary>
    public (long DocumentIndex, long Start, long End) SpanKey => (DocumentIndex, StartCharIndex, EndCharIndex);

    /// <summary>Reads a <c>char_location</c> citation.</summary>
    /// <param name="annotation">The citation, as the provider's chat client reports it.</param>
    /// <returns>The citation.</returns>
    /// <exception cref="NotSupportedException">It is not a well-formed <c>char_location</c> citation.</exception>
    internal static AnswerCitation From(CitationAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        // This app sends only plain-text documents, which are cited by char_location. Any other kind
        // means the request shape has changed.
        string type = TypeOf(annotation);
        if (type != CharLocation)
        {
            throw new NotSupportedException($"A \"{type}\" citation is not supported; plain-text documents cite by char_location.");
        }

        return TryFrom(annotation)
            ?? throw new NotSupportedException("A char_location citation is missing its text or offsets.");
    }

    /// <summary>
    /// Reads the fields of a <c>char_location</c> citation, without checking its type.
    /// </summary>
    /// <param name="annotation">The citation.</param>
    /// <returns>The citation, or <see langword="null"/> when its text or an offset is missing.</returns>
    internal static AnswerCitation? TryFrom(CitationAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        AdditionalPropertiesDictionary? properties = annotation.AdditionalProperties;
        if (annotation.Snippet is not { } citedText
            || properties is null
            || !properties.TryGetValue(ChatKeys.DocumentIndex, out long documentIndex)
            || !properties.TryGetValue(ChatKeys.StartCharIndex, out long start)
            || !properties.TryGetValue(ChatKeys.EndCharIndex, out long end))
        {
            return null;
        }

        return new AnswerCitation(documentIndex, start, end, citedText, annotation.Title);
    }

    /// <summary>The citation's <see cref="ChatKeys.CitationType"/>, or <c>unknown</c> when it has none.</summary>
    /// <param name="annotation">The citation.</param>
    /// <returns>The type.</returns>
    internal static string TypeOf(CitationAnnotation annotation) =>
        annotation.AdditionalProperties is { } properties
        && properties.TryGetValue(ChatKeys.CitationType, out string? type)
        && type is not null
            ? type
            : "unknown";
}
