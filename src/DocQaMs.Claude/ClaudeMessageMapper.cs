using Anthropic.Models.Messages;
using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;

namespace DocQaMs.Claude;

/// <summary>
/// Renders the app's provider-neutral request (<see cref="ChatMessage"/>s, <see cref="AIContent"/> and
/// <see cref="ChatOptions"/> with <see cref="ChatKeys"/>) as Messages API parameters.
/// </summary>
/// <remarks>
/// <para>
/// The mapping reproduces DocQa's request exactly: a plain-string <c>system</c>; plain-text
/// <c>document</c> blocks with a <c>title</c>, a <c>context</c> and citations enabled; text blocks sent
/// verbatim with their <c>char_location</c> citations; thinking blocks with their signatures; redacted
/// thinking with its data; cache breakpoints where <see cref="ChatKeys.CacheTtl"/> asks for one; adaptive
/// thinking; <c>output_config.effort</c>; and a top-level breakpoint from
/// <see cref="ChatKeys.ConversationCacheTtl"/>.
/// </para>
/// <para>
/// Anything the app never sends is refused rather than skipped or approximated, since it would mean the
/// request shape has changed, and a silently different prompt would also miss the cache.
/// </para>
/// </remarks>
internal static class ClaudeMessageMapper
{
    /// <summary>The only citation type plain-text documents are cited by, and the only one replayed.</summary>
    internal const string CharLocationCitationType = "char_location";

    /// <summary>
    /// Renders a user or assistant message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The message parameter, its content always a list of blocks.</returns>
    /// <exception cref="NotSupportedException">
    /// The message is a system or tool message, or has content this app never sends.
    /// </exception>
    public static MessageParam ToMessageParam(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Role role = message.Role == ChatRole.User ? Role.User
            : message.Role == ChatRole.Assistant ? Role.Assistant
            : throw new NotSupportedException(
                $"A \"{message.Role}\" message cannot be sent. Put the system prompt in ChatOptions.Instructions; " +
                "only user and assistant messages are supported.");

        List<ContentBlockParam> content = [.. message.Contents.Select(item => ToContentBlockParam(item, message.Role))];
        return new MessageParam { Role = role, Content = content };
    }

    /// <summary>
    /// Renders one content item as a content block.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <param name="role">The role of the message it belongs to, for error messages.</param>
    /// <returns>The block.</returns>
    /// <exception cref="NotSupportedException">The content is of a kind this app never sends.</exception>
    /// <exception cref="InvalidOperationException">A cache TTL is not <c>5m</c> or <c>1h</c>.</exception>
    public static ContentBlockParam ToContentBlockParam(AIContent content, ChatRole role)
    {
        ArgumentNullException.ThrowIfNull(content);

        switch (content)
        {
            // Already rendered, for example by this mapper on an earlier pass: sent as it is.
            case { RawRepresentation: ContentBlockParam rendered }:
                return rendered;

            case CitableDocumentContent document:
            {
                var block = new DocumentBlockParam
                {
                    Source = new PlainTextSource { Data = document.Text },
                    Title = document.Title,
                    Context = document.Context,
                    Citations = new CitationsConfigParam { Enabled = true },
                };
                return CacheControlOf(document) is { } cacheControl ? block with { CacheControl = cacheControl } : block;
            }

            case TextContent text:
                return ToTextBlockParam(text);

            case TextReasoningContent reasoning when IsRedacted(reasoning):
                return new RedactedThinkingBlockParam
                {
                    Data = reasoning.ProtectedData
                        ?? throw new NotSupportedException("Redacted reasoning has no data (ProtectedData) to send back."),
                };

            case TextReasoningContent reasoning:
                // The API verifies the signature; a thinking block without one would be rejected.
                return new ThinkingBlockParam
                {
                    Thinking = reasoning.Text,
                    Signature = reasoning.ProtectedData
                        ?? throw new NotSupportedException("Reasoning has no signature (ProtectedData) to send back."),
                };

            default:
                throw new NotSupportedException(
                    $"{content.GetType().Name} content in a \"{role}\" message cannot be sent to Claude; this app sends " +
                    $"only {nameof(CitableDocumentContent)}, {nameof(TextContent)} and {nameof(TextReasoningContent)}.");
        }
    }

    /// <summary>
    /// Renders the request-level options: everything but the messages, which the result leaves empty.
    /// </summary>
    /// <param name="options">The options. <see cref="ChatOptions.ModelId"/> and <see cref="ChatOptions.MaxOutputTokens"/> are required.</param>
    /// <returns>
    /// The request parameters: <c>model</c>, <c>max_tokens</c>, and, when the options ask for them, a
    /// plain-string <c>system</c>, <c>thinking</c>, <c>output_config.effort</c> and a top-level
    /// <c>cache_control</c>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The model or output limit is missing, or an effort or cache TTL is not one the API accepts.
    /// </exception>
    public static MessageCreateParams ToCreateParams(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string model = string.IsNullOrEmpty(options.ModelId)
            ? throw new InvalidOperationException("ChatOptions.ModelId is required: the Claude client has no default model.")
            : options.ModelId;
        int maxTokens = options.MaxOutputTokens is { } limit and > 0
            ? limit
            : throw new InvalidOperationException("ChatOptions.MaxOutputTokens is required and must be positive: it is the request's max_tokens.");

        // The fields are set in DocQa's order, which the body keeps: with the adapter filling in the
        // messages in place, the request body is byte-for-byte the one DocQa sent.
        // The system prompt is a plain string, not a text block: it renders first in the cached prefix, and
        // DocQa sent it as a string.
        MessageCreateParams request = options.Instructions is { } instructions
            ? new MessageCreateParams { Model = model, MaxTokens = maxTokens, System = instructions, Messages = [] }
            : new MessageCreateParams { Model = model, MaxTokens = maxTokens, Messages = [] };

        if (options.Reasoning is { } reasoning)
        {
            request = request with { Thinking = ToThinking(reasoning) };
        }

        if (EffortOf(options) is { } effort)
        {
            request = request with { OutputConfig = new OutputConfig { Effort = effort } };
        }

        if (options.AdditionalProperties?.TryGetValue(ChatKeys.ConversationCacheTtl, out string? conversationTtl) == true
            && conversationTtl is not null)
        {
            request = request with { CacheControl = ToCacheControl(conversationTtl) };
        }

        return request;
    }

    /// <summary>Converts a <see cref="ChatKeys.CacheTtl"/> value to a cache breakpoint.</summary>
    /// <exception cref="InvalidOperationException">The TTL is not <c>5m</c> or <c>1h</c>.</exception>
    public static CacheControlEphemeral ToCacheControl(string ttl) => new()
    {
        Ttl = ttl switch
        {
            "5m" => Ttl.Ttl5m,
            "1h" => Ttl.Ttl1h,
            _ => throw new InvalidOperationException($"Unsupported cache TTL \"{ttl}\": expected \"5m\" or \"1h\"."),
        },
    };

    /// <summary>Converts a <see cref="ChatKeys.Effort"/> wire value.</summary>
    /// <exception cref="InvalidOperationException">The value is not an effort level the API accepts.</exception>
    public static Effort ToEffort(string wireValue) => wireValue switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new InvalidOperationException(
            $"Unsupported effort \"{wireValue}\": expected low, medium, high, xhigh or max."),
    };

    private static TextBlockParam ToTextBlockParam(TextContent text)
    {
        // Sent verbatim: an assistant reply's text is replayed exactly as it arrived, whitespace included,
        // so the cached conversation prefix matches.
        var block = new TextBlockParam { Text = text.Text };

        if (text.Annotations is { Count: > 0 } annotations)
        {
            block = block with { Citations = [.. annotations.Select(ToCitationParam)] };
        }

        return CacheControlOf(text) is { } cacheControl ? block with { CacheControl = cacheControl } : block;
    }

    private static TextCitationParam ToCitationParam(AIAnnotation annotation)
    {
        if (annotation is not CitationAnnotation citation)
        {
            throw new NotSupportedException(
                $"A {annotation.GetType().Name} cannot be sent back; only citations of the documents can.");
        }

        AdditionalPropertiesDictionary? properties = citation.AdditionalProperties;
        string type = properties?.TryGetValue(ChatKeys.CitationType, out string? value) == true && value is not null
            ? value
            : "unknown";
        if (type != CharLocationCitationType)
        {
            throw new NotSupportedException(
                $"A \"{type}\" citation cannot be sent back; plain-text documents cite by {CharLocationCitationType}.");
        }

        if (citation.Snippet is not { } citedText
            || !TryGetInt64(properties, ChatKeys.DocumentIndex, out long documentIndex)
            || !TryGetInt64(properties, ChatKeys.StartCharIndex, out long start)
            || !TryGetInt64(properties, ChatKeys.EndCharIndex, out long end))
        {
            throw new NotSupportedException(
                "A char_location citation is missing its cited text or offsets and cannot be sent back.");
        }

        return new CitationCharLocationParam
        {
            CitedText = citedText,
            DocumentIndex = documentIndex,
            DocumentTitle = citation.Title,
            StartCharIndex = start,
            EndCharIndex = end,
        };
    }

    private static ThinkingConfigParam ToThinking(ReasoningOptions reasoning) =>
        reasoning.Effort == ReasoningEffort.None
            ? new ThinkingConfigDisabled()

            // Claude's display is either summarized or omitted; a request for full reasoning gets the summary.
            : new ThinkingConfigAdaptive
            {
                Display = reasoning.Output == ReasoningOutput.None ? Display.Omitted : Display.Summarized,
            };

    /// <summary>
    /// The request's effort: <see cref="ChatKeys.Effort"/> when present, since <see cref="ReasoningEffort"/>
    /// cannot express <c>max</c>, otherwise the nearest reading of <see cref="ChatOptions.Reasoning"/>.
    /// </summary>
    private static Effort? EffortOf(ChatOptions options)
    {
        if (options.AdditionalProperties?.TryGetValue(ChatKeys.Effort, out string? wireValue) == true && wireValue is not null)
        {
            return ToEffort(wireValue);
        }

        return options.Reasoning?.Effort switch
        {
            ReasoningEffort.Low => Effort.Low,
            ReasoningEffort.Medium => Effort.Medium,
            ReasoningEffort.High => Effort.High,
            ReasoningEffort.ExtraHigh => Effort.Xhigh,
            _ => null,
        };
    }

    private static CacheControlEphemeral? CacheControlOf(AIContent content) =>
        content.AdditionalProperties?.TryGetValue(ChatKeys.CacheTtl, out string? ttl) == true && ttl is not null
            ? ToCacheControl(ttl)
            : null;

    private static bool IsRedacted(TextReasoningContent reasoning) =>
        reasoning.AdditionalProperties?.TryGetValue(ChatKeys.RedactedReasoning, out bool redacted) == true && redacted;

    private static bool TryGetInt64(AdditionalPropertiesDictionary? properties, string key, out long value)
    {
        value = 0;
        return properties?.TryGetValue(key, out value) == true;
    }
}
