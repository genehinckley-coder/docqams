using System.Text.Json;
using Anthropic.Models.Messages;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Converts an assistant reply into the parameter blocks that send it back as the next request's
/// history.
/// </summary>
/// <remarks>
/// <para>
/// The C# SDK has no <c>.ToParam()</c>, and a lossy conversion is the likeliest cause of a 400 on a
/// follow-up turn, so every block kind the app can receive is converted field by field:
/// </para>
/// <list type="bullet">
/// <item>a thinking block keeps its signature, which the API verifies;</item>
/// <item>a redacted thinking block keeps its data;</item>
/// <item>a text block keeps each <c>char_location</c> citation;</item>
/// <item>an empty text block is dropped, because the API rejects one on replay. Nothing else is dropped.</item>
/// </list>
/// <para>
/// Any other block or citation kind is refused rather than skipped: this app never requests tools or
/// non-text documents, so one would mean the request shape has changed.
/// </para>
/// </remarks>
public static class ReplayConverter
{
    /// <summary>
    /// Converts a reply's content blocks.
    /// </summary>
    /// <param name="content">The reply's content, in order.</param>
    /// <returns>
    /// The replayable blocks, in order. Empty when nothing can be sent back, for example a refusal with
    /// no text; such a reply must not be committed to the conversation.
    /// </returns>
    /// <exception cref="NotSupportedException">A block or citation is of a kind this app never requests.</exception>
    public static List<ContentBlockParam> ToParams(IReadOnlyList<ContentBlock> content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var blocks = new List<ContentBlockParam>(content.Count);
        foreach (ContentBlock block in content)
        {
            if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                blocks.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                blocks.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickText(out TextBlock? text))
            {
                // A cited answer arrives as many text blocks, some of them empty, and the API rejects an
                // empty text block on replay ("text content blocks must be non-empty").
                if (string.IsNullOrEmpty(text.Text))
                {
                    continue;
                }

                blocks.Add(text.Citations is { Count: > 0 } citations
                    ? new TextBlockParam { Text = text.Text, Citations = [.. citations.Select(ToParam)] }
                    : new TextBlockParam { Text = text.Text });
            }
            else
            {
                throw new NotSupportedException(
                    $"A \"{TypeName(block.Type)}\" content block cannot be replayed; this app never requests one.");
            }
        }

        return blocks;
    }

    private static TextCitationParam ToParam(TextCitation citation)
    {
        if (citation.TryPickCitationCharLocation(out CitationCharLocation? location))
        {
            return new CitationCharLocationParam
            {
                CitedText = location.CitedText,
                DocumentIndex = location.DocumentIndex,
                DocumentTitle = location.DocumentTitle,
                StartCharIndex = location.StartCharIndex,
                EndCharIndex = location.EndCharIndex,
            };
        }

        string type = TypeName(citation.Type);
        if (type != "char_location")
        {
            throw new NotSupportedException(
                $"A \"{type}\" citation cannot be replayed; plain-text documents cite by char_location.");
        }

        // The SDK (12.47.0) does not recognize a char_location citation whose document_title is null, which
        // the API allows for an untitled document. This app always sends titles, so it should not happen;
        // if it does, the citation is rebuilt from its JSON rather than the whole reply being refused.
        AnswerCitation parsed = AnswerCitation.TryFromJson(citation.Json)
            ?? throw new NotSupportedException("A char_location citation is missing its text or offsets and cannot be replayed.");

        return new CitationCharLocationParam
        {
            CitedText = parsed.CitedText,
            DocumentIndex = parsed.DocumentIndex,
            DocumentTitle = parsed.DocumentTitle,
            StartCharIndex = parsed.StartCharIndex,
            EndCharIndex = parsed.EndCharIndex,
        };
    }

    private static string TypeName(JsonElement type) =>
        type.ValueKind == JsonValueKind.String ? type.GetString() ?? "unknown" : "unknown";
}
