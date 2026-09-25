using Anthropic.Models.Messages;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Builds Messages API requests in the prototype's shape, with the model and effort level as
/// parameters.
/// </summary>
/// <remarks>
/// <para>
/// The shape: a plain-string system prompt; <c>messages[0]</c> holding every document block (plain
/// text, with citations enabled and a <c>title</c> and <c>context</c>) followed by the first question;
/// a 1-hour cache breakpoint on the last document block; a top-level 5-minute breakpoint covering the
/// conversation tail; adaptive thinking with summarized display; and <c>output_config.effort</c>.
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
    /// Renders the documents as document blocks, with the corpus cache breakpoint on the last one.
    /// </summary>
    /// <param name="documents">The documents, in the order their <c>document_index</c> values refer to.</param>
    /// <returns>One block per document.</returns>
    /// <exception cref="ArgumentException">There are no documents, or one has an empty text.</exception>
    public static List<ContentBlockParam> DocumentBlocks(IReadOnlyList<RequestDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new ArgumentException("At least one document is required.", nameof(documents));
        }

        var blocks = new List<ContentBlockParam>(documents.Count);
        for (int i = 0; i < documents.Count; i++)
        {
            RequestDocument document = documents[i];
            if (string.IsNullOrEmpty(document.Text))
            {
                throw new ArgumentException($"Document {i} (\"{document.Title}\") has no text.", nameof(documents));
            }

            var block = new DocumentBlockParam
            {
                Source = new PlainTextSource { Data = document.Text },
                Title = document.Title,
                Context = document.Context,
                Citations = new CitationsConfigParam { Enabled = true },
            };

            // One breakpoint closes the whole corpus prefix; the API allows four per request.
            blocks.Add(i == documents.Count - 1
                ? block with { CacheControl = new CacheControlEphemeral { Ttl = ToTtl(CacheAffecting.CorpusCacheTtl) } }
                : block);
        }

        return blocks;
    }

    /// <summary>
    /// Builds <c>messages[0]</c>: every document block, then the question.
    /// </summary>
    /// <remarks>
    /// The API requires the first message to be a user message, so the documents and the first question
    /// necessarily share it.
    /// </remarks>
    /// <param name="documents">The documents.</param>
    /// <param name="question">The first question. Leading and trailing whitespace is removed.</param>
    /// <returns>The first user message.</returns>
    /// <exception cref="ArgumentException">The question is empty or only whitespace.</exception>
    public static MessageParam FirstTurn(IReadOnlyList<RequestDocument> documents, string question)
    {
        string text = RequireQuestion(question);
        List<ContentBlockParam> content = [.. DocumentBlocks(documents), new TextBlockParam { Text = text }];
        return new MessageParam { Role = Role.User, Content = content };
    }

    /// <summary>
    /// Builds a follow-up user message, which carries only the question.
    /// </summary>
    /// <remarks>
    /// The question is a one-block list rather than a bare string, so it serializes the same way whether or
    /// not a later request marks it with the tail breakpoint.
    /// </remarks>
    /// <param name="question">The question. Leading and trailing whitespace is removed.</param>
    /// <returns>The user message.</returns>
    /// <exception cref="ArgumentException">The question is empty or only whitespace.</exception>
    public static MessageParam FollowUpTurn(string question)
    {
        List<ContentBlockParam> content = [new TextBlockParam { Text = RequireQuestion(question) }];
        return new MessageParam { Role = Role.User, Content = content };
    }

    /// <summary>
    /// Builds an assistant message from a reply already converted by <see cref="ReplayConverter"/>.
    /// </summary>
    /// <param name="content">The replayable content blocks.</param>
    /// <returns>The assistant message.</returns>
    /// <exception cref="ArgumentException"><paramref name="content"/> is empty: nothing can be replayed.</exception>
    public static MessageParam AssistantTurn(List<ContentBlockParam> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Count == 0)
        {
            throw new ArgumentException("An assistant turn needs at least one replayable block.", nameof(content));
        }

        return new MessageParam { Role = Role.Assistant, Content = content };
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
    /// <param name="messages">The conversation so far, ending in the new user message.</param>
    /// <returns>The request parameters.</returns>
    /// <exception cref="ArgumentException">An argument is outside what the app offers or sends.</exception>
    public static MessageCreateParams Create(
        string modelId,
        EffortLevel effort,
        long maxTokens,
        string systemPrompt,
        IReadOnlyList<MessageParam> messages)
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
        IReadOnlyList<MessageParam> sent = messages.Count >= 3 ? WithTailBreakpoint(messages, messages.Count - 3) : messages;

        return new MessageCreateParams
        {
            Model = modelId,
            MaxTokens = maxTokens,
            System = systemPrompt,
            Messages = sent,
            Thinking = new ThinkingConfigAdaptive { Display = ToDisplay(CacheAffecting.ThinkingDisplay) },
            OutputConfig = new OutputConfig { Effort = effort.ToSdkEffort() },

            // Automatic caching of the conversation tail. Its shorter TTL lands after the 1-hour document
            // breakpoint, as the API requires.
            CacheControl = new CacheControlEphemeral { Ttl = ToTtl(CacheAffecting.ConversationCacheTtl) },
        };
    }

    /// <summary>
    /// A copy of <paramref name="messages"/> whose question at <paramref name="index"/> carries the
    /// conversation breakpoint. The caller's messages are never changed, so the marker moves forward with
    /// each turn.
    /// </summary>
    private static List<MessageParam> WithTailBreakpoint(IReadOnlyList<MessageParam> messages, int index)
    {
        MessageParam previous = messages[index];
        if (previous.Role.Value() != Role.User)
        {
            throw new ArgumentException($"Message {index} should be the previous question, a user message.", nameof(messages));
        }

        // A question sent as a bare string is refused, not converted: the request that wrote the tail entry
        // sent it as a string, and a block list is not guaranteed to render the same prefix. FirstTurn and
        // FollowUpTurn always build block lists.
        if (!previous.Content.TryPickContentBlockParams(out IReadOnlyList<ContentBlockParam>? blocks)
            || blocks.Count == 0
            || !blocks[^1].TryPickText(out TextBlockParam? question))
        {
            throw new ArgumentException(
                $"Message {index} should be a question built by {nameof(FirstTurn)} or {nameof(FollowUpTurn)}, ending in a text block.",
                nameof(messages));
        }

        List<ContentBlockParam> content = [.. blocks];
        content[^1] = question with { CacheControl = new CacheControlEphemeral { Ttl = ToTtl(CacheAffecting.ConversationCacheTtl) } };

        List<MessageParam> marked = [.. messages];
        marked[index] = previous with { Content = content };
        return marked;
    }

    private static string RequireQuestion(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        string text = question.Trim();

        // The API rejects an empty text block, so this fails before any call is made.
        return text.Length > 0 ? text : throw new ArgumentException("The question is empty.", nameof(question));
    }

    private static Ttl ToTtl(string ttl) => ttl switch
    {
        "1h" => Ttl.Ttl1h,
        "5m" => Ttl.Ttl5m,
        _ => throw new InvalidOperationException($"Unsupported cache TTL \"{ttl}\"."),
    };

    private static Display ToDisplay(string display) => display switch
    {
        "summarized" => Display.Summarized,
        "omitted" => Display.Omitted,
        _ => throw new InvalidOperationException($"Unsupported thinking display \"{display}\"."),
    };
}
