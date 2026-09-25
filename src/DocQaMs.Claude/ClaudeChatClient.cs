using System.Runtime.CompilerServices;
using System.Text;
using Anthropic.Models.Messages;
using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocQaMs.Claude;

/// <summary>
/// The Claude middleware: sits over the Anthropic SDK's <see cref="IChatClient"/> adapter
/// (<c>AsIChatClient()</c>) and carries what the app needs that the adapter drops or cannot express.
/// </summary>
/// <remarks>
/// <para>
/// <b>Requests.</b> Every content item is rendered as the SDK's <see cref="ContentBlockParam"/> by
/// <see cref="ClaudeMessageMapper"/> and handed to the adapter as <see cref="AIContent.RawRepresentation"/>,
/// which the adapter sends verbatim. The model, output limit, system prompt, thinking, effort and top-level
/// cache breakpoint go through <see cref="ChatOptions.RawRepresentationFactory"/>, on a copy of the
/// caller's options with <see cref="ChatOptions.Instructions"/> cleared, since the adapter would otherwise
/// append the instructions as a text block and the <c>system</c> field would no longer be the plain string
/// the cache prefix was written with. The resulting request body is the one DocQa sent. A caller's own
/// <see cref="ChatOptions.RawRepresentationFactory"/> is replaced.
/// </para>
/// <para>
/// <b>System messages</b> are refused with <see cref="NotSupportedException"/>: the system prompt is
/// <see cref="ChatOptions.Instructions"/>, and folding a system message into it would silently change the
/// cached prefix.
/// </para>
/// <para>
/// <b>Responses</b> follow the streaming contract in <c>docs/MIGRATION.md</c> (see
/// <see cref="ClaudeStreamTranslator"/>): block indexes, citation offsets, usage at message start with the
/// cache-write split, and the raw stop reason and refusal details. A failure after <c>message_stop</c> ends
/// the stream normally, logged as a warning, because the whole response had already arrived; a stream that
/// ends without <c>message_stop</c> fails with <see cref="ChatProviderFailure.StreamEndedEarly"/>. Other SDK
/// failures become <see cref="ChatProviderException"/>; cancellation, including the SDK's own timeout,
/// propagates unchanged as <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public sealed class ClaudeChatClient : DelegatingChatClient
{
    private readonly ILogger _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="innerClient">The SDK's adapter, from <c>AsIChatClient()</c>, possibly wrapped by other middleware that passes updates through unchanged.</param>
    /// <param name="logger">Receives a warning when a stream breaks after the whole response arrived.</param>
    public ClaudeChatClient(IChatClient innerClient, ILogger? logger = null)
        : base(innerClient)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Implemented over the streaming path, so a non-streamed response has exactly the same request, the
    /// same translation and the same failures, and is not subject to the SDK's client-side limit on the
    /// output a non-streamed request may ask for. The reply is one assistant message holding one content
    /// item per content block, in block order, each with its <see cref="ChatKeys.BlockIndex"/>: reasoning
    /// with its text and signature (or redacted data), and text with its citations. Unlike
    /// <see cref="ChatResponseExtensions.ToChatResponse(IEnumerable{ChatResponseUpdate})"/>, it neither merges
    /// adjacent blocks nor adds the input-side usage to the final usage.
    /// </remarks>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var aggregator = new ResponseAggregator();
        await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            aggregator.Add(update);
        }

        return aggregator.ToResponse();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The options lack a model or output limit, or carry an unsupported effort or cache TTL.</exception>
    /// <exception cref="NotSupportedException">A message or content item is of a kind this app never sends.</exception>
    /// <exception cref="ChatProviderException">The API, or the connection to it, failed.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled or timed out.</exception>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        (List<ChatMessage> rendered, ChatOptions renderedOptions) = Render(messages, options);
        var translator = new ClaudeStreamTranslator();

        IAsyncEnumerator<ChatResponseUpdate> stream = base.GetStreamingResponseAsync(rendered, renderedOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using (stream.ConfigureAwait(false))
        {
            while (true)
            {
                // A yield cannot sit inside a try with a catch, so each step is read here and yielded below.
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (Exception ex) when (translator.MessageStopped)
                {
                    // The whole response has arrived; only closing the stream failed. DocQa treated this the
                    // same way, whatever the exception.
                    _logger.LogWarning(ex, "The response arrived whole, but closing its stream failed.");
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ClaudeFailures.Translate(ex) is { } failure)
                {
                    throw failure;
                }

                yield return translator.Translate(update);
            }
        }

        // The SDK's raw stream (12.50.0) ends quietly when the body ends early, where DocQa's aggregator
        // threw: without message_stop, the response is incomplete, whatever arrived.
        if (!translator.MessageStopped)
        {
            throw new ChatProviderException(
                ChatProviderFailure.StreamEndedEarly,
                "The response stream ended before message_stop: the response is incomplete.");
        }

        if (translator.FinalUpdate() is { } final)
        {
            yield return final;
        }
    }

    /// <summary>
    /// Renders the request for the adapter: every content item as a <see cref="ContentBlockParam"/>, and
    /// the options as a <see cref="MessageCreateParams"/> factory on a copy of the caller's options.
    /// </summary>
    private static (List<ChatMessage> Messages, ChatOptions Options) Render(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        List<ChatMessage> rendered = [];
        foreach (ChatMessage message in messages)
        {
            MessageParam param = ClaudeMessageMapper.ToMessageParam(message);
            param.Content.TryPickContentBlockParams(out IReadOnlyList<ContentBlockParam>? blocks);
            rendered.Add(new ChatMessage(
                message.Role,
                [.. (blocks ?? []).Select(block => new AIContent { RawRepresentation = block })]));
        }

        MessageCreateParams request = ClaudeMessageMapper.ToCreateParams(options);
        ChatOptions copy = options.Clone();
        copy.Instructions = null;
        copy.RawRepresentationFactory = _ => request;
        return (rendered, copy);
    }

    /// <summary>
    /// Builds a <see cref="ChatResponse"/> from translated updates, one content item per content block.
    /// </summary>
    private sealed class ResponseAggregator
    {
        private readonly List<Block> _blocks = [];
        private readonly Dictionary<(long Index, bool Reasoning), Block> _byIndex = [];
        private readonly ChatResponse _response = new();
        private string? _messageId;

        public void Add(ChatResponseUpdate update)
        {
            _response.ResponseId ??= update.ResponseId;
            _response.ModelId ??= update.ModelId;
            _response.CreatedAt ??= update.CreatedAt;
            _response.FinishReason = update.FinishReason ?? _response.FinishReason;
            _messageId ??= update.MessageId;

            if (update.AdditionalProperties is { Count: > 0 } properties)
            {
                AdditionalPropertiesDictionary merged = _response.AdditionalProperties ??= [];
                foreach (KeyValuePair<string, object?> property in properties)
                {
                    merged[property.Key] = property.Value;
                }
            }

            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    // The last usage is the complete one; the first is only the input side.
                    case UsageContent usage:
                        _response.Usage = usage.Details;
                        break;

                    case TextContent or TextReasoningContent
                        when content.AdditionalProperties?.TryGetValue(ChatKeys.BlockIndex, out long index) == true:
                        BlockFor(index, content).Append(content);
                        break;

                    default:
                        _blocks.Add(new Block(content));
                        break;
                }
            }
        }

        public ChatResponse ToResponse()
        {
            _response.Messages = [new ChatMessage(ChatRole.Assistant, [.. _blocks.Select(block => block.ToContent())]) { MessageId = _messageId }];
            return _response;
        }

        private Block BlockFor(long index, AIContent first)
        {
            (long, bool) key = (index, first is TextReasoningContent);
            if (!_byIndex.TryGetValue(key, out Block? block))
            {
                block = new Block(first, index);
                _byIndex[key] = block;
                _blocks.Add(block);
            }

            return block;
        }

        /// <summary>One content block's deltas, or one content item that belongs to no block.</summary>
        private sealed class Block
        {
            private readonly AIContent _first;
            private readonly long? _index;
            private readonly StringBuilder _text = new();
            private readonly List<AIAnnotation> _annotations = [];
            private readonly AdditionalPropertiesDictionary _properties = [];
            private string? _protectedData;

            public Block(AIContent first, long? index = null)
            {
                _first = first;
                _index = index;
            }

            public void Append(AIContent content)
            {
                if (content.AdditionalProperties is { } properties)
                {
                    foreach (KeyValuePair<string, object?> property in properties)
                    {
                        _properties[property.Key] = property.Value;
                    }
                }

                if (content.Annotations is { } annotations)
                {
                    _annotations.AddRange(annotations);
                }

                switch (content)
                {
                    case TextContent text:
                        _text.Append(text.Text);
                        break;

                    case TextReasoningContent reasoning:
                        _text.Append(reasoning.Text);

                        // The signature arrives in the last delta; the block's start carries an empty one.
                        if (!string.IsNullOrEmpty(reasoning.ProtectedData))
                        {
                            _protectedData = reasoning.ProtectedData;
                        }

                        break;
                }
            }

            public AIContent ToContent()
            {
                if (_index is null)
                {
                    return _first;
                }

                AIContent content = _first is TextReasoningContent
                    ? new TextReasoningContent(_text.ToString()) { ProtectedData = _protectedData }
                    : new TextContent(_text.ToString());
                content.AdditionalProperties = _properties;
                if (_annotations.Count > 0)
                {
                    content.Annotations = [.. _annotations];
                }

                return content;
            }
        }
    }
}
