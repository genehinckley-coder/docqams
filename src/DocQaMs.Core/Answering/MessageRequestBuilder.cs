using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Builds chat requests in the prototype's shape, with the model and effort level as parameters.
/// </summary>
/// <remarks>
/// <para>
/// The shape: a plain-string system prompt (<see cref="ChatOptions.Instructions"/>); <c>messages[0]</c>
/// holding every document (a <see cref="CitableDocumentContent"/>: plain text, with citations enabled and a
/// title and context) followed by the first question; a 1-hour cache breakpoint on the last document; a
/// top-level 5-minute breakpoint covering the conversation tail (<see cref="ChatKeys.ConversationCacheTtl"/>);
/// adaptive thinking with summarized display (<see cref="ChatOptions.Reasoning"/>); and the effort level
/// (<see cref="ChatKeys.Effort"/>). The provider's chat client renders each of these as its API expects.
/// </para>
/// <para>
/// One addition to the prototype's shape: on a follow-up, the previous question also carries an explicit
/// 5-minute breakpoint. The previous request's top-level breakpoint wrote its entry ending exactly there,
/// but a breakpoint looks back at most 20 block positions for an earlier entry, and a cited answer spans
/// more than that. Without the marker, every follow-up would rewrite the whole conversation history
/// instead of reading it (found live by plan item 1, check 4). That makes three of the four breakpoints a
/// request may carry.
/// </para>
/// <para>
/// Everything before the first question is identical on every request for a given model and level,
/// which is what lets the corpus cache entry be read.
/// </para>
/// </remarks>
public static class MessageRequestBuilder
{
    /// <summary>
    /// Renders the documents as citable document contents, with the corpus cache breakpoint on the last one.
    /// </summary>
    /// <param name="documents">The documents, in the order their <see cref="ChatKeys.DocumentIndex"/> values refer to.</param>
    /// <returns>One <see cref="CitableDocumentContent"/> per document.</returns>
    /// <exception cref="ArgumentException">There are no documents, or one has an empty text.</exception>
    public static List<AIContent> DocumentBlocks(IReadOnlyList<RequestDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new ArgumentException("At least one document is required.", nameof(documents));
        }

        var blocks = new List<AIContent>(documents.Count);
        for (int i = 0; i < documents.Count; i++)
        {
            RequestDocument document = documents[i];
            if (string.IsNullOrEmpty(document.Text))
            {
                throw new ArgumentException($"Document {i} (\"{document.Title}\") has no text.", nameof(documents));
            }

            var block = new CitableDocumentContent(document.Title, document.Context, document.Text);

            // One breakpoint closes the whole corpus prefix; the API allows four per request.
            if (i == documents.Count - 1)
            {
                block.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ChatKeys.CacheTtl] = RequireTtl(CacheAffecting.CorpusCacheTtl),
                };
            }

            blocks.Add(block);
        }

        return blocks;
    }

    /// <summary>
    /// Builds <c>messages[0]</c>: every document, then the question.
    /// </summary>
    /// <remarks>
    /// The API requires the first message to be a user message, so the documents and the first question
    /// necessarily share it.
    /// </remarks>
    /// <param name="documents">The documents.</param>
    /// <param name="question">The first question. Leading and trailing whitespace is removed.</param>
    /// <returns>The first user message.</returns>
    /// <exception cref="ArgumentException">The question is empty or only whitespace.</exception>
    public static ChatMessage FirstTurn(IReadOnlyList<RequestDocument> documents, string question)
    {
        string text = RequireQuestion(question);
        List<AIContent> content = [.. DocumentBlocks(documents), new TextContent(text)];
        return new ChatMessage(ChatRole.User, content);
    }

    /// <summary>
    /// Builds a follow-up user message, which carries only the question.
    /// </summary>
    /// <param name="question">The question. Leading and trailing whitespace is removed.</param>
    /// <returns>The user message: one <see cref="TextContent"/>.</returns>
    /// <exception cref="ArgumentException">The question is empty or only whitespace.</exception>
    public static ChatMessage FollowUpTurn(string question)
    {
        List<AIContent> content = [new TextContent(RequireQuestion(question))];
        return new ChatMessage(ChatRole.User, content);
    }

    /// <summary>
    /// Builds an assistant message from a reply already converted by <see cref="ReplayConverter"/>.
    /// </summary>
    /// <param name="content">The replayable contents.</param>
    /// <returns>The assistant message.</returns>
    /// <exception cref="ArgumentException"><paramref name="content"/> is empty: nothing can be replayed.</exception>
    public static ChatMessage AssistantTurn(List<AIContent> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Count == 0)
        {
            throw new ArgumentException("An assistant turn needs at least one replayable block.", nameof(content));
        }

        return new ChatMessage(ChatRole.Assistant, content);
    }

    /// <summary>
    /// Builds the complete request around <paramref name="messages"/>.
    /// </summary>
    /// <param name="modelId">A selectable model id.</param>
    /// <param name="effort">The effort level.</param>
    /// <param name="maxTokens">The output limit; thinking counts toward it.</param>
    /// <param name="systemPrompt">
    /// The system prompt: <see cref="CacheAffecting.SystemPrompt"/> in production. It renders before the
    /// documents, so any variation in it invalidates the corpus cache entry.
    /// </param>
    /// <param name="messages">The conversation so far, ending in the new user message. It is not changed.</param>
    /// <returns>The messages as sent, and the options.</returns>
    /// <exception cref="ArgumentException">An argument is outside what the app offers or sends.</exception>
    /// <exception cref="OverflowException"><paramref name="maxTokens"/> does not fit in an <see cref="int"/>.</exception>
    public static ChatRequest Create(
        string modelId,
        EffortLevel effort,
        long maxTokens,
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);

        if (!CacheAffecting.SelectableModels.Any(model => model.Id == modelId))
        {
            throw new ArgumentException($"\"{modelId}\" is not a selectable model.", nameof(modelId));
        }

        if (!CacheAffecting.EffortLevels.Contains(effort))
        {
            throw new ArgumentException($"{effort} is not a selectable effort level.", nameof(effort));
        }

        if (messages.Count == 0)
        {
            throw new ArgumentException("A request needs at least one message.", nameof(messages));
        }

        // With history, messages[^3] is the previous question: the request before this one ended there.
        IReadOnlyList<ChatMessage> sent = messages.Count >= 3 ? WithTailBreakpoint(messages, messages.Count - 3) : [.. messages];

        var options = new ChatOptions
        {
            ModelId = modelId,
            MaxOutputTokens = checked((int)maxTokens),
            Instructions = systemPrompt,
            Reasoning = new ReasoningOptions
            {
                Effort = ToReasoningEffort(effort),
                Output = ToReasoningOutput(CacheAffecting.ThinkingDisplay),
            },
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                // Authoritative: ReasoningEffort has no max.
                [ChatKeys.Effort] = effort.ToWireValue(),

                // Automatic caching of the conversation tail. Its shorter TTL lands after the 1-hour document
                // breakpoint, as the API requires.
                [ChatKeys.ConversationCacheTtl] = RequireTtl(CacheAffecting.ConversationCacheTtl),
            },
        };

        return new ChatRequest(sent, options);
    }

    /// <summary>
    /// A copy of <paramref name="messages"/> whose question at <paramref name="index"/> carries the
    /// conversation breakpoint. The caller's messages and contents are never changed, so the marker moves
    /// forward with each turn.
    /// </summary>
    private static List<ChatMessage> WithTailBreakpoint(IReadOnlyList<ChatMessage> messages, int index)
    {
        ChatMessage previous = messages[index] ?? throw new ArgumentException($"Message {index} is null.", nameof(messages));
        if (previous.Role != ChatRole.User)
        {
            throw new ArgumentException($"Message {index} should be the previous question, a user message.", nameof(messages));
        }

        // FirstTurn and FollowUpTurn always end the question in a text content; anything else was not built
        // here, and is not guaranteed to render the prefix the previous request cached.
        if (previous.Contents.Count == 0 || previous.Contents[^1] is not TextContent question)
        {
            throw new ArgumentException(
                $"Message {index} should be a question built by {nameof(FirstTurn)} or {nameof(FollowUpTurn)}, ending in a text content.",
                nameof(messages));
        }

        AdditionalPropertiesDictionary properties = question.AdditionalProperties?.Clone() ?? [];
        properties[ChatKeys.CacheTtl] = RequireTtl(CacheAffecting.ConversationCacheTtl);

        List<AIContent> content = [.. previous.Contents];
        content[^1] = new TextContent(question.Text)
        {
            AdditionalProperties = properties,
            Annotations = question.Annotations is null ? null : [.. question.Annotations],
        };

        ChatMessage marked = previous.Clone();
        marked.Contents = content;

        List<ChatMessage> copy = [.. messages];
        copy[index] = marked;
        return copy;
    }

    private static string RequireQuestion(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        string text = question.Trim();

        // The API rejects an empty text block, so this fails before any call is made.
        return text.Length > 0 ? text : throw new ArgumentException("The question is empty.", nameof(question));
    }

    private static string RequireTtl(string ttl) => ttl is "1h" or "5m"
        ? ttl
        : throw new InvalidOperationException($"Unsupported cache TTL \"{ttl}\".");

    /// <summary>
    /// The nearest <see cref="ReasoningEffort"/>. It has no <c>max</c>, so <see cref="EffortLevel.Max"/> maps to
    /// <see cref="ReasoningEffort.ExtraHigh"/>; <see cref="ChatKeys.Effort"/> carries the exact level.
    /// </summary>
    private static ReasoningEffort ToReasoningEffort(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => ReasoningEffort.Low,
        EffortLevel.Medium => ReasoningEffort.Medium,
        EffortLevel.High => ReasoningEffort.High,
        EffortLevel.XHigh or EffortLevel.Max => ReasoningEffort.ExtraHigh,
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Not a defined effort level."),
    };

    private static ReasoningOutput ToReasoningOutput(string display) => display switch
    {
        "summarized" => ReasoningOutput.Summary,
        "omitted" => ReasoningOutput.None,
        _ => throw new InvalidOperationException($"Unsupported thinking display \"{display}\"."),
    };
}
