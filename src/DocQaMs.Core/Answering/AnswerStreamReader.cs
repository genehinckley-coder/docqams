using System.Text;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Turns the chat client's streamed updates into <see cref="AnswerEvent"/>s, numbers citations as they
/// arrive, and aggregates the final response. One instance reads one response.
/// </summary>
/// <remarks>
/// <para>
/// Every response content carries its content block's <see cref="ChatKeys.BlockIndex"/>. A text block is
/// complete when an update marks its end (<see cref="ChatKeys.CompletedBlockIndex"/>), or failing that once
/// content with a higher index arrives, or when the stream ends (<see cref="Finish"/>); its
/// <see cref="AnswerEvent.TextBlockCompleted"/> then carries the citations that arrived for it.
/// </para>
/// <para>
/// The reply is aggregated per block rather than by <c>ToChatResponse()</c>, which would merge adjacent text
/// blocks and lose which citations belong to which block: each block becomes one content of the final
/// message, in block order, with its citations and thinking signature intact.
/// </para>
/// </remarks>
internal sealed class AnswerStreamReader
{
    private readonly ModelPrices _prices;

    // Every block in the order it started, which is block order; and the same blocks by index.
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<int, Block> _blocksByIndex = [];

    // The text blocks that have started but not yet completed, with their citations as numbered sources.
    private readonly SortedDictionary<int, List<AnswerCitation>> _openTextBlocks = [];
    private readonly CitationNumbering _numbering = new();

    private Block? _current;
    private bool _usageStarted;
    private UsageDetails? _usage;
    private ChatFinishReason? _finishReason;
    private string? _stopReason;
    private string? _refusalCategory;
    private string? _refusalExplanation;
    private string? _modelId;
    private string? _responseId;
    private string? _messageId;
    private DateTimeOffset? _createdAt;

    public AnswerStreamReader(ModelPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        _prices = prices;
    }

    /// <summary>Reads one streamed update.</summary>
    /// <param name="update">The update.</param>
    /// <returns>The answer events it produces: often one, sometimes none, sometimes more.</returns>
    /// <exception cref="NotSupportedException">
    /// A citation is not a well-formed <c>char_location</c> citation, or two kinds of content claim one block.
    /// </exception>
    public IReadOnlyList<AnswerEvent> Read(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        ReadMetadata(update);

        List<AnswerEvent> events = [];
        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case UsageContent usage:
                    ReadUsage(usage, events);
                    break;
                case TextReasoningContent reasoning:
                    ReadReasoning(reasoning, events);
                    break;
                case TextContent text:
                    ReadText(text, events);
                    break;
                case null:
                    break;
                default:
                    // Not something this app requests. It is kept in the reply, where the replay conversion
                    // refuses it, so the reply is shown but not committed.
                    _blocks.Add(new Block(BlockKind.Other, _current?.Index ?? 0) { Other = content });
                    break;
            }
        }

        // A block's end, when the provider marks it: complete it now, so a Stop or failure before the next block
        // still leaves it complete.
        if (update.AdditionalProperties is { } properties
            && properties.TryGetValue(ChatKeys.CompletedBlockIndex, out long completed))
        {
            events.AddRange(CompleteTextBlock(checked((int)completed)));
        }

        return events;
    }

    /// <summary>
    /// Completes every text block still open: the stream has ended. Call once, after the last update.
    /// </summary>
    /// <returns>A <see cref="AnswerEvent.TextBlockCompleted"/> per open text block, in block order.</returns>
    public IReadOnlyList<AnswerEvent> Finish() => CompleteTextBlocksBefore(int.MaxValue);

    /// <summary>
    /// The aggregated response: one assistant message with one content per block, in block order; the finish
    /// reason; the last usage reported; the model and response ids; and the provider's stop reason and
    /// refusal details under their <see cref="ChatKeys"/>.
    /// </summary>
    /// <returns>A new response. The reader's state is not changed.</returns>
    public ChatResponse Response()
    {
        List<AIContent> contents = [.. _blocks.Select(block => block.ToContent())];
        var message = new ChatMessage(ChatRole.Assistant, contents) { MessageId = _messageId, CreatedAt = _createdAt };

        AdditionalPropertiesDictionary? properties = null;
        AddProperty(ref properties, ChatKeys.StopReason, _stopReason);
        AddProperty(ref properties, ChatKeys.RefusalCategory, _refusalCategory);
        AddProperty(ref properties, ChatKeys.RefusalExplanation, _refusalExplanation);

        return new ChatResponse(message)
        {
            FinishReason = _finishReason,
            Usage = _usage,
            ModelId = _modelId,
            ResponseId = _responseId,
            CreatedAt = _createdAt,
            AdditionalProperties = properties,
        };
    }

    /// <summary>
    /// The distinct cited spans in the order first cited, after numbering any citation in
    /// <paramref name="response"/> that the stream did not deliver as part of a completed block. Normally
    /// that adds nothing, since every cited block has already completed; it keeps the list complete if one
    /// did not, as the prototype does.
    /// </summary>
    /// <param name="response">The final response.</param>
    /// <returns>The sources. Marker <c>[n]</c> is element <c>n - 1</c>.</returns>
    /// <exception cref="NotSupportedException">A citation is not a well-formed <c>char_location</c> citation.</exception>
    public IReadOnlyList<AnswerCitation> Sources(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (AIContent content in response.Messages.SelectMany(message => message.Contents))
        {
            if (content is TextContent { Annotations: { Count: > 0 } annotations })
            {
                _numbering.Number(annotations.OfType<CitationAnnotation>().Select(AnswerCitation.From));
            }
        }

        return _numbering.Sources;
    }

    private void ReadMetadata(ChatResponseUpdate update)
    {
        _modelId = update.ModelId ?? _modelId;
        _responseId = update.ResponseId ?? _responseId;
        _messageId = update.MessageId ?? _messageId;
        _createdAt ??= update.CreatedAt;
        _finishReason = update.FinishReason ?? _finishReason;

        if (update.AdditionalProperties is { } properties)
        {
            _stopReason = StringProperty(properties, ChatKeys.StopReason) ?? _stopReason;
            _refusalCategory = StringProperty(properties, ChatKeys.RefusalCategory) ?? _refusalCategory;
            _refusalExplanation = StringProperty(properties, ChatKeys.RefusalExplanation) ?? _refusalExplanation;
        }
    }

    private void ReadUsage(UsageContent usage, List<AnswerEvent> events)
    {
        // The first usage carries the input side; the last carries the final counts.
        _usage = usage.Details;
        if (!_usageStarted)
        {
            _usageStarted = true;
            TokenUsage input = TokenUsage.InputSide(usage.Details);
            events.Add(new AnswerEvent.UsageStarted(input, TurnCost.For(input, _prices)));
        }
    }

    private void ReadReasoning(TextReasoningContent reasoning, List<AnswerEvent> events)
    {
        bool redacted = ReplayConverter.IsRedacted(reasoning);
        Block block = BlockFor(reasoning, BlockKind.Reasoning, events, out _);
        if (!string.IsNullOrEmpty(reasoning.ProtectedData))
        {
            block.ProtectedData = reasoning.ProtectedData;
        }

        if (redacted)
        {
            // Its content is encrypted: it is announced once, on its first content, and only its data is kept.
            if (!block.Redacted)
            {
                block.Redacted = true;
                events.Add(new AnswerEvent.ThinkingRedacted());
            }

            return;
        }

        if (!string.IsNullOrEmpty(reasoning.Text))
        {
            block.Text.Append(reasoning.Text);
            events.Add(new AnswerEvent.ThinkingDelta(reasoning.Text));
        }
    }

    private void ReadText(TextContent text, List<AnswerEvent> events)
    {
        Block block = BlockFor(text, BlockKind.Text, events, out bool started);
        if (started)
        {
            _openTextBlocks[block.Index] = [];
        }

        if (!string.IsNullOrEmpty(text.Text))
        {
            block.Text.Append(text.Text);
            events.Add(new AnswerEvent.TextDelta(block.Index, text.Text));
        }

        foreach (CitationAnnotation citation in (text.Annotations ?? []).OfType<CitationAnnotation>())
        {
            // Read first, so a citation this app cannot check fails the answer rather than being kept.
            AnswerCitation read = AnswerCitation.From(citation);
            block.Citations.Add(citation);
            if (_openTextBlocks.TryGetValue(block.Index, out List<AnswerCitation>? open))
            {
                open.Add(read);
            }
        }
    }

    /// <summary>
    /// The block <paramref name="content"/> belongs to, started if it is new. Any text block with a lower
    /// index is complete, and its <see cref="AnswerEvent.TextBlockCompleted"/> is added first.
    /// </summary>
    private Block BlockFor(AIContent content, BlockKind kind, List<AnswerEvent> events, out bool started)
    {
        int index = IndexOf(content, kind);
        events.AddRange(CompleteTextBlocksBefore(index));

        if (_blocksByIndex.TryGetValue(index, out Block? block))
        {
            if (block.Kind != kind)
            {
                throw new NotSupportedException(
                    $"Content block {index} carries both {block.Kind} and {kind} content; this app cannot read it.");
            }

            started = false;
        }
        else
        {
            block = new Block(kind, index);
            _blocksByIndex[index] = block;
            _blocks.Add(block);
            started = true;
        }

        _current = block;
        return block;
    }

    /// <summary>
    /// The content's <see cref="ChatKeys.BlockIndex"/>. The provider always sets one; without it, the content
    /// continues the current block when it is of the same kind, and starts the next one otherwise.
    /// </summary>
    private int IndexOf(AIContent content, BlockKind kind)
    {
        if (content.AdditionalProperties is { } properties && properties.TryGetValue(ChatKeys.BlockIndex, out long index))
        {
            return checked((int)index);
        }

        return _current switch
        {
            null => 0,
            { } current when current.Kind == kind => current.Index,
            { } current => current.Index + 1,
        };
    }

    private List<AnswerEvent> CompleteTextBlocksBefore(int index)
    {
        List<AnswerEvent> events = [];
        foreach (int open in _openTextBlocks.Keys.Where(open => open < index).ToList())
        {
            events.AddRange(CompleteTextBlock(open));
        }

        return events;
    }

    /// <summary>Completes the text block at <paramref name="index"/>, if it is open; otherwise does nothing.</summary>
    private List<AnswerEvent> CompleteTextBlock(int index)
    {
        if (!_openTextBlocks.Remove(index, out List<AnswerCitation>? citations))
        {
            return [];
        }

        return [new AnswerEvent.TextBlockCompleted(index, citations.AsReadOnly(), _numbering.Number(citations))];
    }

    private static string? StringProperty(AdditionalPropertiesDictionary properties, string key) =>
        properties.TryGetValue(key, out string? value) ? value : null;

    private static void AddProperty(ref AdditionalPropertiesDictionary? properties, string key, string? value)
    {
        if (value is not null)
        {
            (properties ??= [])[key] = value;
        }
    }

    private enum BlockKind
    {
        Reasoning,
        Text,
        Other,
    }

    /// <summary>One content block as it accumulates.</summary>
    private sealed class Block(BlockKind kind, int index)
    {
        public BlockKind Kind { get; } = kind;

        public int Index { get; } = index;

        public StringBuilder Text { get; } = new();

        public List<AIAnnotation> Citations { get; } = [];

        public string? ProtectedData { get; set; }

        public bool Redacted { get; set; }

        /// <summary>For <see cref="BlockKind.Other"/>: the content as it arrived.</summary>
        public AIContent? Other { get; init; }

        public AIContent ToContent()
        {
            if (Kind == BlockKind.Other)
            {
                return Other!;
            }

            var properties = new AdditionalPropertiesDictionary { [ChatKeys.BlockIndex] = (long)Index };
            if (Kind == BlockKind.Reasoning)
            {
                if (Redacted)
                {
                    properties[ChatKeys.RedactedReasoning] = true;
                }

                return new TextReasoningContent(Text.ToString()) { ProtectedData = ProtectedData, AdditionalProperties = properties };
            }

            return new TextContent(Text.ToString())
            {
                Annotations = Citations.Count > 0 ? [.. Citations] : null,
                AdditionalProperties = properties,
            };
        }
    }

    /// <summary>
    /// Numbers distinct cited spans in the order first cited, as the prototype's renderer does.
    /// </summary>
    private sealed class CitationNumbering
    {
        private readonly Dictionary<(long, long, long), int> _numbers = [];
        private readonly List<AnswerCitation> _sources = [];

        public IReadOnlyList<AnswerCitation> Sources => _sources.AsReadOnly();

        /// <summary>Numbers each span not seen before; returns these citations' numbers, once each.</summary>
        public IReadOnlyList<int> Number(IEnumerable<AnswerCitation> citations)
        {
            List<int> numbers = [];
            foreach (AnswerCitation citation in citations)
            {
                if (!_numbers.TryGetValue(citation.SpanKey, out int number))
                {
                    _sources.Add(citation);
                    number = _sources.Count;
                    _numbers[citation.SpanKey] = number;
                }

                if (!numbers.Contains(number))
                {
                    numbers.Add(number);
                }
            }

            return numbers.AsReadOnly();
        }
    }
}
