using System.Text.Json.Nodes;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// Builds Messages API stream events, as JSON strings for <see cref="Sse"/>, for one answer at a time.
/// </summary>
internal static class StreamEvents
{
    public const string Title = "Part A: Alpha Rules";

    /// <summary>A whole answer: <c>message_start</c>, the given blocks, then <see cref="End"/> with <c>end_turn</c>.</summary>
    public static List<string> Answer(params IEnumerable<string>[] blocks) => [Start(), .. blocks.SelectMany(block => block), .. End()];

    /// <param name="omitBreakdownField">A field to leave out of <c>cache_creation</c>, for a partial breakdown.</param>
    public static string Start(
        long input = 12, long write1h = 2_080, long write5m = 20, long read = 0, bool breakdown = true, string? omitBreakdownField = null)
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = input,
            ["cache_creation_input_tokens"] = write1h + write5m,
            ["cache_read_input_tokens"] = read,
            ["output_tokens"] = 1,
        };
        if (breakdown)
        {
            usage["cache_creation"] = new JsonObject
            {
                ["ephemeral_1h_input_tokens"] = write1h,
                ["ephemeral_5m_input_tokens"] = write5m,
            };
            if (omitBreakdownField is not null)
            {
                ((JsonObject)usage["cache_creation"]!).Remove(omitBreakdownField);
            }
        }

        return new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = "msg_test",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = "claude-opus-5",
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["stop_details"] = null,
                ["usage"] = usage,
            },
        }.ToJsonString();
    }

    public static IEnumerable<string> Thinking(int index, string thinking, string signature = "c2lnbmF0dXJl")
    {
        yield return BlockStart(index, new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" });
        yield return BlockDelta(index, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking });
        yield return BlockDelta(index, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature });
        yield return BlockStop(index);
    }

    /// <summary>A text block: its citations first, then its text, as the API can order them.</summary>
    public static IEnumerable<string> Text(int index, string text, params JsonObject[] citations)
    {
        yield return BlockStart(index, new JsonObject { ["type"] = "text", ["text"] = "", ["citations"] = null });
        foreach (JsonObject citation in citations)
        {
            yield return BlockDelta(index, new JsonObject { ["type"] = "citations_delta", ["citation"] = citation.DeepClone() });
        }

        if (text.Length > 0)
        {
            yield return BlockDelta(index, new JsonObject { ["type"] = "text_delta", ["text"] = text });
        }

        yield return BlockStop(index);
    }

    public static JsonObject Citation(long start, long end, string citedText = "cited text", long documentIndex = 0, string? title = Title) => new()
    {
        ["type"] = "char_location",
        ["cited_text"] = citedText,
        ["document_index"] = documentIndex,
        ["document_title"] = title,
        ["start_char_index"] = start,
        ["end_char_index"] = end,
    };

    public static IEnumerable<string> End(string? stopReason = "end_turn", long output = 57, JsonObject? stopDetails = null)
    {
        yield return new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null,
                ["stop_details"] = stopDetails?.DeepClone(),
            },
            ["usage"] = new JsonObject { ["output_tokens"] = output },
        }.ToJsonString();
        yield return """{"type":"message_stop"}""";
    }

    /// <summary>An <c>error</c> event, as the API sends when it fails partway through a stream.</summary>
    public static string Error(string type = "overloaded_error", string message = "Overloaded") =>
        new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
        }.ToJsonString();

    private static string BlockStart(int index, JsonObject block) =>
        new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block }.ToJsonString();

    private static string BlockDelta(int index, JsonObject delta) =>
        new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta }.ToJsonString();

    private static string BlockStop(int index) =>
        new JsonObject { ["type"] = "content_block_stop", ["index"] = index }.ToJsonString();
}
