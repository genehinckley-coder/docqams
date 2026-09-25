using System.Text.Json;
using Anthropic.Models.Messages;

namespace DocQaMs.Core.Answering;

/// <summary>
/// One <c>char_location</c> citation from an answer: a span of one plain-text document as sent.
/// </summary>
/// <remarks>
/// Read from the citation's JSON rather than through the SDK's typed union: the SDK (12.47.0) does not
/// recognize a <c>char_location</c> citation whose <c>document_title</c> is null, which the API allows
/// (found by plan item 1). The offsets are code points, not UTF-16 units; convert them through the
/// document's code-point index before slicing.
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
    /// <summary>
    /// Identifies the cited span. A span cited more than once is one source, with one number.
    /// </summary>
    public (long DocumentIndex, long Start, long End) SpanKey => (DocumentIndex, StartCharIndex, EndCharIndex);

    /// <summary>Reads a citation from a completed text block.</summary>
    /// <exception cref="NotSupportedException">It is not a well-formed <c>char_location</c> citation.</exception>
    internal static AnswerCitation From(TextCitation citation) => FromJson(citation.Json);

    /// <summary>Reads a citation from a <c>citations_delta</c> stream event.</summary>
    /// <exception cref="NotSupportedException">It is not a well-formed <c>char_location</c> citation.</exception>
    internal static AnswerCitation From(Citation citation) => FromJson(citation.Json);

    /// <summary>
    /// Reads the fields of a <c>char_location</c> citation, without checking its <c>type</c>.
    /// </summary>
    /// <returns>The citation, or <see langword="null"/> when its text or an offset is missing.</returns>
    internal static AnswerCitation? TryFromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("cited_text", out JsonElement citedText) || citedText.ValueKind != JsonValueKind.String
            || !TryGetInt64(json, "document_index", out long documentIndex)
            || !TryGetInt64(json, "start_char_index", out long start)
            || !TryGetInt64(json, "end_char_index", out long end))
        {
            return null;
        }

        string? title = json.TryGetProperty("document_title", out JsonElement titleElement)
            && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString()
                : null;

        return new AnswerCitation(documentIndex, start, end, citedText.GetString()!, title);
    }

    private static AnswerCitation FromJson(JsonElement json)
    {
        string type = json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("type", out JsonElement typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? "unknown"
                : "unknown";

        // This app sends only plain-text documents, which are cited by char_location. Any other kind
        // means the request shape has changed.
        if (type != "char_location")
        {
            throw new NotSupportedException($"A \"{type}\" citation is not supported; plain-text documents cite by char_location.");
        }

        return TryFromJson(json)
            ?? throw new NotSupportedException("A char_location citation is missing its text or offsets.");
    }

    private static bool TryGetInt64(JsonElement json, string name, out long value)
    {
        value = 0;
        return json.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }
}
