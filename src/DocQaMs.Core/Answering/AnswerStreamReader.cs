using Anthropic.Models.Messages;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Turns the SDK's raw stream events into <see cref="AnswerEvent"/>s, and numbers citations as they
/// arrive. One instance reads one response.
/// </summary>
internal sealed class AnswerStreamReader
{
    private readonly ModelPrices _prices;

    // The citations of each text block that has started but not yet stopped, by block index.
    private readonly Dictionary<long, List<AnswerCitation>> _openTextBlocks = [];
    private readonly CitationNumbering _numbering = new();

    public AnswerStreamReader(ModelPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        _prices = prices;
    }

    /// <summary>The refusal details from the last <c>message_delta</c>, if any.</summary>
    public RefusalStopDetails? StopDetails { get; private set; }

    /// <summary>Whether <c>message_stop</c> has arrived: the whole response has been received.</summary>
    public bool MessageStopped { get; private set; }

    /// <summary>Reads one raw event.</summary>
    /// <returns>The answer events it produces: usually one, sometimes none.</returns>
    /// <exception cref="NotSupportedException">A citation is not a well-formed <c>char_location</c> citation.</exception>
    public IReadOnlyList<AnswerEvent> Read(RawMessageStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        if (streamEvent.TryPickStart(out RawMessageStartEvent? start))
        {
            TokenUsage usage = TokenUsage.InputSide(start.Message.Usage);
            return [new AnswerEvent.UsageStarted(usage, TurnCost.For(usage, _prices))];
        }

        if (streamEvent.TryPickContentBlockStart(out RawContentBlockStartEvent? blockStart))
        {
            return ReadBlockStart(blockStart);
        }

        if (streamEvent.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta))
        {
            return ReadDelta(delta);
        }

        if (streamEvent.TryPickContentBlockStop(out RawContentBlockStopEvent? blockStop)
            && _openTextBlocks.Remove(blockStop.Index, out List<AnswerCitation>? citations))
        {
            return [new AnswerEvent.TextBlockCompleted(BlockIndex(blockStop.Index), citations, _numbering.Number(citations))];
        }

        if (streamEvent.TryPickDelta(out RawMessageDeltaEvent? messageDelta) && messageDelta.Delta.StopDetails is { } details)
        {
            StopDetails = details;
        }

        if (streamEvent.TryPickStop(out _))
        {
            MessageStopped = true;
        }

        return [];
    }

    /// <summary>
    /// The distinct cited spans in the order first cited, after numbering any citation in
    /// <paramref name="message"/> that the stream did not deliver. Normally that adds nothing, since every
    /// cited block has already passed through <c>content_block_stop</c>; it keeps the list complete if one
    /// did not, as the prototype does.
    /// </summary>
    public IReadOnlyList<AnswerCitation> Sources(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        foreach (ContentBlock block in message.Content)
        {
            if (block.TryPickText(out TextBlock? text) && text.Citations is { Count: > 0 } cited)
            {
                _numbering.Number(cited.Select(AnswerCitation.From));
            }
        }

        return _numbering.Sources;
    }

    private IReadOnlyList<AnswerEvent> ReadBlockStart(RawContentBlockStartEvent blockStart)
    {
        if (blockStart.ContentBlock.TryPickText(out TextBlock? text))
        {
            _openTextBlocks[blockStart.Index] = [.. (text.Citations ?? []).Select(AnswerCitation.From)];

            // The API starts every text block empty; this keeps any text it might carry.
            return string.IsNullOrEmpty(text.Text) ? [] : [new AnswerEvent.TextDelta(BlockIndex(blockStart.Index), text.Text)];
        }

        if (blockStart.ContentBlock.TryPickRedactedThinking(out _))
        {
            return [new AnswerEvent.ThinkingRedacted()];
        }

        if (blockStart.ContentBlock.TryPickThinking(out ThinkingBlock? thinking) && !string.IsNullOrEmpty(thinking.Thinking))
        {
            return [new AnswerEvent.ThinkingDelta(thinking.Thinking)];
        }

        return [];
    }

    private IReadOnlyList<AnswerEvent> ReadDelta(RawContentBlockDeltaEvent delta)
    {
        if (delta.Delta.TryPickText(out TextDelta? text))
        {
            return string.IsNullOrEmpty(text.Text) ? [] : [new AnswerEvent.TextDelta(BlockIndex(delta.Index), text.Text)];
        }

        if (delta.Delta.TryPickThinking(out ThinkingDelta? thinking))
        {
            return string.IsNullOrEmpty(thinking.Thinking) ? [] : [new AnswerEvent.ThinkingDelta(thinking.Thinking)];
        }

        if (delta.Delta.TryPickCitations(out CitationsDelta? citation)
            && _openTextBlocks.TryGetValue(delta.Index, out List<AnswerCitation>? citations))
        {
            citations.Add(AnswerCitation.From(citation.Citation));
        }

        // Signature deltas matter only for replay, and the aggregator keeps them in the final message.
        return [];
    }

    private static int BlockIndex(long index) => checked((int)index);

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
