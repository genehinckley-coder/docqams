using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Converts an assistant reply into the contents that send it back as the next request's history.
/// </summary>
/// <remarks>
/// <para>
/// A lossy conversion is the likeliest cause of a 400 on a follow-up turn, so every content kind the app
/// can receive is copied field by field, into new contents that carry only what the provider's chat client
/// replays:
/// </para>
/// <list type="bullet">
/// <item>reasoning keeps its text and its signature (<see cref="TextReasoningContent.ProtectedData"/>), which
/// the API verifies;</item>
/// <item>redacted reasoning keeps its data and its <see cref="ChatKeys.RedactedReasoning"/> marker;</item>
/// <item>text keeps each <c>char_location</c> citation, as a <see cref="CitationAnnotation"/> with the
/// <see cref="ChatKeys"/> offsets;</item>
/// <item>empty text is dropped, because the API rejects an empty text block on replay. Nothing else is
/// dropped.</item>
/// </list>
/// <para>
/// Any other content or citation kind is refused rather than skipped: this app never requests tools or
/// non-text documents, so one would mean the request shape has changed.
/// </para>
/// </remarks>
public static class ReplayConverter
{
    /// <summary>
    /// Converts a reply.
    /// </summary>
    /// <param name="reply">The assistant message: <see cref="AnswerResult.Reply"/>.</param>
    /// <returns>
    /// The replayable contents, in order. Empty when nothing can be sent back, for example a refusal with
    /// no text; such a reply must not be committed to the conversation.
    /// </returns>
    /// <exception cref="NotSupportedException">A content or citation is of a kind this app never requests.</exception>
    public static List<AIContent> ToReplay(ChatMessage reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return ToReplay(reply.Contents.AsReadOnly());
    }

    /// <summary>
    /// Converts a reply's contents.
    /// </summary>
    /// <param name="content">The reply's contents, in order.</param>
    /// <returns>
    /// The replayable contents, in order. Empty when nothing can be sent back, for example a refusal with
    /// no text; such a reply must not be committed to the conversation.
    /// </returns>
    /// <exception cref="NotSupportedException">A content or citation is of a kind this app never requests.</exception>
    public static List<AIContent> ToReplay(IReadOnlyList<AIContent> content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var replay = new List<AIContent>(content.Count);
        foreach (AIContent block in content)
        {
            switch (block)
            {
                case TextReasoningContent reasoning when IsRedacted(reasoning):
                    replay.Add(new TextReasoningContent("")
                    {
                        ProtectedData = reasoning.ProtectedData,
                        AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.RedactedReasoning] = true },
                    });
                    break;

                case TextReasoningContent reasoning:
                    replay.Add(new TextReasoningContent(reasoning.Text) { ProtectedData = reasoning.ProtectedData });
                    break;

                case TextContent text:
                    // A cited answer arrives as many text blocks, some of them empty, and the API rejects an
                    // empty text block on replay ("text content blocks must be non-empty").
                    if (string.IsNullOrEmpty(text.Text))
                    {
                        continue;
                    }

                    List<AIAnnotation> citations = [.. (text.Annotations ?? []).Select(ToReplay)];
                    replay.Add(citations.Count > 0
                        ? new TextContent(text.Text) { Annotations = citations }
                        : new TextContent(text.Text));
                    break;

                default:
                    throw new NotSupportedException(
                        $"A \"{block?.GetType().Name ?? "null"}\" content block cannot be replayed; this app never requests one.");
            }
        }

        return replay;
    }

    /// <summary>Whether <paramref name="reasoning"/> carries the <see cref="ChatKeys.RedactedReasoning"/> marker.</summary>
    internal static bool IsRedacted(TextReasoningContent reasoning) =>
        reasoning.AdditionalProperties is { } properties
        && properties.TryGetValue(ChatKeys.RedactedReasoning, out bool redacted)
        && redacted;

    private static CitationAnnotation ToReplay(AIAnnotation annotation)
    {
        if (annotation is not CitationAnnotation citation)
        {
            throw new NotSupportedException(
                $"A \"{annotation?.GetType().Name ?? "null"}\" annotation cannot be replayed; plain-text documents cite by char_location.");
        }

        string type = AnswerCitation.TypeOf(citation);
        if (type != AnswerCitation.CharLocation)
        {
            throw new NotSupportedException(
                $"A \"{type}\" citation cannot be replayed; plain-text documents cite by char_location.");
        }

        AnswerCitation parsed = AnswerCitation.TryFrom(citation)
            ?? throw new NotSupportedException("A char_location citation is missing its text or offsets and cannot be replayed.");

        return new CitationAnnotation
        {
            Snippet = parsed.CitedText,
            Title = parsed.DocumentTitle,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatKeys.CitationType] = AnswerCitation.CharLocation,
                [ChatKeys.DocumentIndex] = parsed.DocumentIndex,
                [ChatKeys.StartCharIndex] = parsed.StartCharIndex,
                [ChatKeys.EndCharIndex] = parsed.EndCharIndex,
            },
        };
    }
}
