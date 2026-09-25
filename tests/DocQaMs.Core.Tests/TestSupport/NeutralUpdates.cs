using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>The input side of a response's usage, in billing buckets.</summary>
/// <param name="Input">Uncached input tokens.</param>
/// <param name="Write1h">Tokens written to the cache with the 1-hour TTL.</param>
/// <param name="Write5m">Tokens written with the 5-minute TTL.</param>
/// <param name="Read">Tokens read from the cache.</param>
internal sealed record InputTokens(long Input = 12, long Write1h = 2_080, long Write5m = 20, long Read = 0)
{
    public static InputTokens Default { get; } = new();
}

/// <summary>
/// Builds streamed <see cref="ChatResponseUpdate"/>s the way a provider's chat client emits them under the
/// app's streaming contract (docs/MIGRATION.md): usage at the start and at the end with the <see cref="ChatKeys"/>
/// counts, every text and reasoning content tagged with its <see cref="ChatKeys.BlockIndex"/>, citations as an
/// empty text content carrying one <see cref="CitationAnnotation"/>, and the finish reason with the raw stop
/// reason.
/// </summary>
internal static class NeutralUpdates
{
    public const string Title = "Part A: Alpha Rules";

    public const string ResponseId = "msg_test";

    public const string ModelId = "claude-opus-5";

    /// <summary>A whole answer: <see cref="Start"/>, the given blocks, then <see cref="End"/> with <c>end_turn</c>.</summary>
    public static List<ChatResponseUpdate> Answer(params IEnumerable<ChatResponseUpdate>[] blocks) =>
        [Start(), .. blocks.SelectMany(block => block), .. End()];

    /// <summary>The first update: the input side of the usage, with no output yet.</summary>
    public static ChatResponseUpdate Start(InputTokens? tokens = null) =>
        Update(new UsageContent(Usage(tokens ?? InputTokens.Default, output: 0)));

    /// <summary>
    /// Usage in the Microsoft.Extensions.AI convention: <see cref="UsageDetails.InputTokenCount"/> counts every
    /// input token, the cache reads are <see cref="UsageDetails.CachedInputTokenCount"/>, and the writes are split
    /// by TTL in <see cref="UsageDetails.AdditionalCounts"/>.
    /// </summary>
    public static UsageDetails Usage(InputTokens tokens, long output)
    {
        long input = tokens.Input + tokens.Write1h + tokens.Write5m + tokens.Read;
        return new UsageDetails
        {
            InputTokenCount = input,
            CachedInputTokenCount = tokens.Read,
            OutputTokenCount = output,
            TotalTokenCount = input + output,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                [ChatKeys.CacheWrite1hTokens] = tokens.Write1h,
                [ChatKeys.CacheWrite5mTokens] = tokens.Write5m,
            },
        };
    }

    /// <summary>A thinking block: its text in one delta, then its signature.</summary>
    public static IEnumerable<ChatResponseUpdate> Thinking(int index, string thinking, string signature = "c2lnbmF0dXJl")
    {
        yield return ThinkingDelta(index, thinking);
        yield return Signature(index, signature);
    }

    public static ChatResponseUpdate ThinkingDelta(int index, string thinking) =>
        Update(Tagged(new TextReasoningContent(thinking), index));

    public static ChatResponseUpdate Signature(int index, string signature) =>
        Update(Tagged(new TextReasoningContent("") { ProtectedData = signature }, index));

    /// <summary>A redacted thinking block: no text, only its opaque data.</summary>
    public static ChatResponseUpdate RedactedThinking(int index, string data)
    {
        var content = Tagged(new TextReasoningContent("") { ProtectedData = data }, index);
        content.AdditionalProperties![ChatKeys.RedactedReasoning] = true;
        return Update(content);
    }

    /// <summary>
    /// A text block: its citations first, then its text, as the API can order them. A block with neither arrives
    /// as one empty delta, as its start does.
    /// </summary>
    public static IEnumerable<ChatResponseUpdate> Text(int index, string text, params CitationAnnotation[] citations)
    {
        if (text.Length == 0 && citations.Length == 0)
        {
            yield return TextDelta(index, "");
            yield break;
        }

        foreach (CitationAnnotation citation in citations)
        {
            yield return CitationDelta(index, citation);
        }

        if (text.Length > 0)
        {
            yield return TextDelta(index, text);
        }
    }

    public static ChatResponseUpdate TextDelta(int index, string text) => Update(Tagged(new TextContent(text), index));

    /// <summary>The end of a content block, as the contract marks it: an update with no contents.</summary>
    public static ChatResponseUpdate BlockStop(int index) =>
        new(ChatRole.Assistant, [])
        {
            ResponseId = ResponseId,
            MessageId = ResponseId,
            ModelId = ModelId,
            AdditionalProperties = new() { [ChatKeys.CompletedBlockIndex] = (long)index },
        };

    /// <summary>A citation as the contract carries it: an empty text content with the block's index and one annotation.</summary>
    public static ChatResponseUpdate CitationDelta(int index, CitationAnnotation citation) =>
        Update(Tagged(new TextContent("") { Annotations = [citation] }, index));

    /// <summary>A <c>char_location</c> citation, with the neutral keys.</summary>
    public static CitationAnnotation Citation(
        long start, long end, string citedText = "cited text", long documentIndex = 0, string? title = Title) => new()
        {
            Snippet = citedText,
            Title = title,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatKeys.CitationType] = "char_location",
                [ChatKeys.DocumentIndex] = documentIndex,
                [ChatKeys.StartCharIndex] = start,
                [ChatKeys.EndCharIndex] = end,
            },
        };

    /// <summary>
    /// A citation carrying only what is given: a <paramref name="type"/> other than <c>char_location</c>, or one
    /// missing an offset.
    /// </summary>
    public static CitationAnnotation RawCitation(
        string type, string? citedText, long? documentIndex = 0, long? start = null, long? end = null, string? title = null)
    {
        var properties = new AdditionalPropertiesDictionary { [ChatKeys.CitationType] = type };
        AddIfPresent(properties, ChatKeys.DocumentIndex, documentIndex);
        AddIfPresent(properties, ChatKeys.StartCharIndex, start);
        AddIfPresent(properties, ChatKeys.EndCharIndex, end);
        return new CitationAnnotation { Snippet = citedText, Title = title, AdditionalProperties = properties };
    }

    /// <summary>
    /// The end of the message: the finish reason with the raw stop reason (and any refusal details), then the
    /// final usage.
    /// </summary>
    /// <param name="stopReason">The raw stop reason, or <see langword="null"/> for none at all.</param>
    public static IEnumerable<ChatResponseUpdate> End(
        string? stopReason = "end_turn",
        long output = 57,
        InputTokens? tokens = null,
        string? refusalCategory = null,
        string? refusalExplanation = null)
    {
        var finish = new ChatResponseUpdate(ChatRole.Assistant, []) { ResponseId = ResponseId, MessageId = ResponseId, ModelId = ModelId };
        if (stopReason is not null)
        {
            finish.FinishReason = FinishReasonOf(stopReason);
            finish.AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.StopReason] = stopReason };
            if (refusalCategory is not null)
            {
                finish.AdditionalProperties[ChatKeys.RefusalCategory] = refusalCategory;
            }

            if (refusalExplanation is not null)
            {
                finish.AdditionalProperties[ChatKeys.RefusalExplanation] = refusalExplanation;
            }
        }

        yield return finish;
        yield return Update(new UsageContent(Usage(tokens ?? InputTokens.Default, output)));
    }

    /// <summary>The finish reason a raw stop reason summarizes.</summary>
    public static ChatFinishReason? FinishReasonOf(string stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "refusal" => ChatFinishReason.ContentFilter,
        _ => new ChatFinishReason(stopReason),
    };

    private static ChatResponseUpdate Update(AIContent content) =>
        new(ChatRole.Assistant, [content]) { ResponseId = ResponseId, MessageId = ResponseId, ModelId = ModelId };

    private static T Tagged<T>(T content, int index)
        where T : AIContent
    {
        content.AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.BlockIndex] = (long)index };
        return content;
    }

    private static void AddIfPresent(AdditionalPropertiesDictionary properties, string key, long? value)
    {
        if (value is { } present)
        {
            properties[key] = present;
        }
    }
}
