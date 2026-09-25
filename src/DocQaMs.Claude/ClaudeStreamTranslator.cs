using System.Text.Json;
using Anthropic.Models.Messages;
using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;

namespace DocQaMs.Claude;

/// <summary>
/// Adds what the SDK's <see cref="IChatClient"/> adapter drops to each of its streamed updates, reading the
/// raw stream event each update carries. One instance reads one response.
/// </summary>
/// <remarks>
/// <para>
/// The adapter yields exactly one update per server-sent event, with the SDK's
/// <see cref="RawMessageStreamEvent"/> as its <see cref="ChatResponseUpdate.RawRepresentation"/>, then one
/// last update, with no raw event, carrying its own usage. This class, per the streaming contract in
/// <c>docs/MIGRATION.md</c>:
/// </para>
/// <list type="bullet">
/// <item>tags every text and reasoning content with <see cref="ChatKeys.BlockIndex"/>, and marks the update
/// for each <c>content_block_stop</c> with <see cref="ChatKeys.CompletedBlockIndex"/>;</item>
/// <item>replaces the adapter's redacted-thinking marker with <see cref="ChatKeys.RedactedReasoning"/>;</item>
/// <item>replaces each citation with one read from the citation's JSON, carrying the document index and
/// code-point offsets the adapter drops;</item>
/// <item>adds the input side of the usage at <c>message_start</c>, before any output streams;</item>
/// <item>adds the raw stop reason and refusal details to the update that carries the finish reason;</item>
/// <item>replaces the adapter's final usage, which lacks the cache-write split, with its own.</item>
/// </list>
/// <para>
/// Citations and the usage breakdown are read from JSON rather than through the SDK's typed unions: the
/// SDK (12.47.0) did not recognize a <c>char_location</c> citation whose <c>document_title</c> is null,
/// which the API allows, and a missing breakdown field should read as unknown rather than as zero.
/// </para>
/// </remarks>
internal sealed class ClaudeStreamTranslator
{
    /// <summary>The adapter's marker on reasoning content that came from a <c>redacted_thinking</c> block.</summary>
    internal const string AdapterRedactedThinkingKey = "anthropic:redacted_thinking";

    private UsageCounts? _start;
    private UsageCounts? _final;

    /// <summary>Whether <c>message_stop</c> has arrived: the whole response has been received.</summary>
    public bool MessageStopped { get; private set; }

    /// <summary>Whether the final usage has been yielded, in place of the adapter's.</summary>
    public bool FinalUsageYielded { get; private set; }

    /// <summary>The finish reason, once <c>message_delta</c> has set one.</summary>
    public ChatFinishReason? FinishReason { get; private set; }

    /// <summary>The response's message id, once <c>message_start</c> has given it.</summary>
    public string? MessageId { get; private set; }

    /// <summary>The model that answered, once <c>message_start</c> has given it.</summary>
    public string? ModelId { get; private set; }

    /// <summary>
    /// Translates one of the adapter's updates in place.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <returns><paramref name="update"/>.</returns>
    public ChatResponseUpdate Translate(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        MessageId ??= update.MessageId;
        ModelId ??= update.ModelId;
        FinishReason = update.FinishReason ?? FinishReason;

        // The adapter's own usage lacks the cache-write split and arrives only at the end; it is replaced.
        bool hadAdapterUsage = update.Contents.Any(content => content is UsageContent);
        List<AIContent> contents = [.. update.Contents.Where(content => content is not UsageContent)];

        if (update.RawRepresentation is RawMessageStreamEvent streamEvent)
        {
            TranslateEvent(streamEvent, update, contents);
        }
        else if (hadAdapterUsage && FinalUsage() is { } final)
        {
            contents.Add(new UsageContent(final));
            FinalUsageYielded = true;
        }

        update.Contents = contents;
        return update;
    }

    /// <summary>
    /// The update that ends a stream whose final usage was never yielded, because the stream broke after
    /// <c>message_stop</c>; <see langword="null"/> when there is nothing to report.
    /// </summary>
    public ChatResponseUpdate? FinalUpdate()
    {
        if (FinalUsageYielded || FinalUsage() is not { } final)
        {
            return null;
        }

        FinalUsageYielded = true;
        return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(final)])
        {
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = FinishReason,
            MessageId = MessageId,
            ModelId = ModelId,
            ResponseId = MessageId,
        };
    }

    private void TranslateEvent(RawMessageStreamEvent streamEvent, ChatResponseUpdate update, List<AIContent> contents)
    {
        if (streamEvent.TryPickStart(out RawMessageStartEvent? start))
        {
            _start = UsageCounts.From(start.Message.Usage.RawData);
            contents.Add(new UsageContent(_start.ToUsageDetails(outputTokens: 0)));
        }
        else if (streamEvent.TryPickContentBlockStart(out RawContentBlockStartEvent? blockStart))
        {
            // The API starts every text block without citations, but a block that did carry some would
            // otherwise lose them: the adapter drops them from content_block_start.
            if (blockStart.ContentBlock.TryPickText(out _)
                && TryGetProperty(blockStart.ContentBlock.Json, "citations", JsonValueKind.Array, out JsonElement citations))
            {
                foreach (JsonElement citation in citations.EnumerateArray())
                {
                    contents.Add(CitationContent(citation));
                }
            }

            TagBlock(contents, blockStart.Index);
        }
        else if (streamEvent.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta))
        {
            if (TryGetProperty(delta.Delta.Json, "type", JsonValueKind.String, out JsonElement type)
                && type.GetString() == "citations_delta")
            {
                // The adapter's citation has neither the document index nor the offsets; this one has both.
                contents.RemoveAll(content => content is TextContent);
                if (TryGetProperty(delta.Delta.Json, "citation", JsonValueKind.Object, out JsonElement citation))
                {
                    contents.Add(CitationContent(citation));
                }
            }

            TagBlock(contents, delta.Index);
        }
        else if (streamEvent.TryPickContentBlockStop(out RawContentBlockStopEvent? blockStop))
        {
            // The adapter yields nothing for a block's end; the app completes the block here, as DocQa did.
            (update.AdditionalProperties ??= [])[ChatKeys.CompletedBlockIndex] = blockStop.Index;
        }
        else if (streamEvent.TryPickDelta(out RawMessageDeltaEvent? messageDelta))
        {
            AddStopDetails(update, messageDelta.Delta.RawData);
            if (_start is not null && messageDelta.Usage is { } deltaUsage)
            {
                _final = _start.Update(deltaUsage.RawData);
            }
        }
        else if (streamEvent.TryPickStop(out _))
        {
            MessageStopped = true;
        }
    }

    /// <summary>The complete usage: the input side from <c>message_start</c>, updated by <c>message_delta</c>.</summary>
    private UsageDetails? FinalUsage() =>
        _final is not null ? _final.ToUsageDetails(_final.Output)
        : _start?.ToUsageDetails(outputTokens: 0);

    private static void TagBlock(List<AIContent> contents, long index)
    {
        foreach (AIContent content in contents)
        {
            if (content is not (TextContent or TextReasoningContent))
            {
                continue;
            }

            AdditionalPropertiesDictionary properties = content.AdditionalProperties ??= [];
            properties[ChatKeys.BlockIndex] = index;

            if (properties.Remove(AdapterRedactedThinkingKey))
            {
                properties[ChatKeys.RedactedReasoning] = true;
            }
        }
    }

    /// <summary>A citation as the contract carries it: an empty text content with one annotation. The caller tags its block.</summary>
    private static TextContent CitationContent(JsonElement citation)
    {
        var annotation = new CitationAnnotation
        {
            Snippet = GetString(citation, "cited_text"),
            Title = GetString(citation, "document_title"),
            AdditionalProperties = [],
        };

        // Only what the citation actually carries: a missing type or offset stays missing, so the app can
        // tell an uncheckable citation from one that points at the start of a document.
        annotation.AdditionalProperties[ChatKeys.CitationType] = GetString(citation, "type") ?? "unknown";
        AddInt64(annotation.AdditionalProperties, ChatKeys.DocumentIndex, citation, "document_index");
        AddInt64(annotation.AdditionalProperties, ChatKeys.StartCharIndex, citation, "start_char_index");
        AddInt64(annotation.AdditionalProperties, ChatKeys.EndCharIndex, citation, "end_char_index");

        return new TextContent(string.Empty) { Annotations = [annotation] };
    }

    private static void AddStopDetails(ChatResponseUpdate update, IReadOnlyDictionary<string, JsonElement> delta)
    {
        if (!delta.TryGetValue("stop_reason", out JsonElement stopReason) || stopReason.ValueKind != JsonValueKind.String)
        {
            return;
        }

        AdditionalPropertiesDictionary properties = update.AdditionalProperties ??= [];
        properties[ChatKeys.StopReason] = stopReason.GetString();

        if (delta.TryGetValue("stop_details", out JsonElement details) && details.ValueKind == JsonValueKind.Object)
        {
            if (GetString(details, "category") is { } category)
            {
                properties[ChatKeys.RefusalCategory] = category;
            }

            if (GetString(details, "explanation") is { } explanation)
            {
                properties[ChatKeys.RefusalExplanation] = explanation;
            }
        }
    }

    private static bool TryGetProperty(JsonElement json, string name, JsonValueKind kind, out JsonElement value)
    {
        value = default;
        return json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty(name, out value)
            && value.ValueKind == kind;
    }

    private static string? GetString(JsonElement json, string name) =>
        TryGetProperty(json, name, JsonValueKind.String, out JsonElement value) ? value.GetString() : null;

    private static long? GetInt64(JsonElement json, string name) =>
        TryGetProperty(json, name, JsonValueKind.Number, out JsonElement value) && value.TryGetInt64(out long number)
            ? number
            : null;

    private static void AddInt64(AdditionalPropertiesDictionary properties, string key, JsonElement json, string name)
    {
        if (GetInt64(json, name) is { } value)
        {
            properties[key] = value;
        }
    }

    /// <summary>
    /// A response's token counts in the API's own buckets, read from the usage JSON.
    /// </summary>
    /// <param name="Input">Uncached input tokens.</param>
    /// <param name="CacheWrite">Tokens written to the cache, at either TTL.</param>
    /// <param name="CacheWrite1h">The 1-hour part of <paramref name="CacheWrite"/>, when the breakdown gives it.</param>
    /// <param name="CacheWrite5m">The 5-minute part of <paramref name="CacheWrite"/>, when the breakdown gives it.</param>
    /// <param name="CacheRead">Tokens read from the cache.</param>
    /// <param name="Output">Output tokens, thinking included.</param>
    private sealed record UsageCounts(
        long Input, long CacheWrite, long? CacheWrite1h, long? CacheWrite5m, long CacheRead, long Output)
    {
        public static UsageCounts From(IReadOnlyDictionary<string, JsonElement> usage)
        {
            JsonElement breakdown = usage.TryGetValue("cache_creation", out JsonElement value) ? value : default;
            return new UsageCounts(
                Count(usage, "input_tokens") ?? 0,
                Count(usage, "cache_creation_input_tokens") ?? 0,
                GetInt64(breakdown, "ephemeral_1h_input_tokens"),
                GetInt64(breakdown, "ephemeral_5m_input_tokens"),
                Count(usage, "cache_read_input_tokens") ?? 0,
                Count(usage, "output_tokens") ?? 0);
        }

        /// <summary>
        /// Applies <c>message_delta</c>'s usage, whose counts are cumulative: its output count is final,
        /// and any input field it repeats replaces the one from <c>message_start</c>.
        /// </summary>
        public UsageCounts Update(IReadOnlyDictionary<string, JsonElement> delta)
        {
            JsonElement breakdown = delta.TryGetValue("cache_creation", out JsonElement value) ? value : default;
            return new UsageCounts(
                Count(delta, "input_tokens") ?? Input,
                Count(delta, "cache_creation_input_tokens") ?? CacheWrite,
                GetInt64(breakdown, "ephemeral_1h_input_tokens") ?? CacheWrite1h,
                GetInt64(breakdown, "ephemeral_5m_input_tokens") ?? CacheWrite5m,
                Count(delta, "cache_read_input_tokens") ?? CacheRead,
                Count(delta, "output_tokens") ?? Output);
        }

        /// <summary>
        /// Converts to the Microsoft.Extensions.AI convention: input counts every input token, cached input
        /// counts the reads, and the additional counts split the writes by TTL.
        /// </summary>
        /// <remarks>
        /// A request carries a 1-hour and a 5-minute breakpoint. Any part of the write the TTL breakdown does
        /// not account for, all of it when the breakdown is missing, is counted at the 1-hour TTL: the higher
        /// price, and the one the corpus pays, so the cost errs high rather than low. This is DocQa's
        /// <c>TokenUsage.From</c>, unchanged.
        /// </remarks>
        public UsageDetails ToUsageDetails(long outputTokens)
        {
            long write5m = CacheWrite5m ?? 0;
            long write1h = Math.Max(CacheWrite1h ?? 0, CacheWrite - write5m);
            long input = Input + CacheRead + write1h + write5m;
            return new UsageDetails
            {
                InputTokenCount = input,
                CachedInputTokenCount = CacheRead,
                OutputTokenCount = outputTokens,
                TotalTokenCount = input + outputTokens,
                AdditionalCounts = new()
                {
                    [ChatKeys.CacheWrite1hTokens] = write1h,
                    [ChatKeys.CacheWrite5mTokens] = write5m,
                },
            };
        }

        private static long? Count(IReadOnlyDictionary<string, JsonElement> usage, string name) =>
            usage.TryGetValue(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long count)
                ? count
                : null;
    }
}
