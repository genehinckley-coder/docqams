using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Chat;

/// <summary>
/// The <see cref="AdditionalPropertiesDictionary"/> keys the app and its chat provider agree on, for what
/// Microsoft.Extensions.AI has no first-class member for.
/// </summary>
/// <remarks>
/// <para>
/// The app talks to the model only through <see cref="IChatClient"/>. Its answers depend on a few
/// capabilities the abstraction does not model: cache breakpoints, per-block citation offsets into the
/// source documents, cache-write durations and the provider's raw stop reason. Each is carried under one
/// of these keys; the provider's chat client (DocQaMs.Claude) reads the request-side keys and writes the
/// response-side ones. Nothing else in the app knows which provider answers.
/// </para>
/// <para>
/// Integer values are written as <see cref="long"/>; read them with
/// <see cref="AdditionalPropertiesDictionary{TValue}.TryGetValue{T}(string, out T)"/>, which converts.
/// </para>
/// </remarks>
public static class ChatKeys
{
    // ---- Request side: ChatOptions.AdditionalProperties -------------------------------------------------

    /// <summary>
    /// On <see cref="ChatOptions.AdditionalProperties"/>: the effort level's wire value (<c>low</c>,
    /// <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>). Authoritative over
    /// <see cref="ChatOptions.Reasoning"/>, whose <see cref="ReasoningEffort"/> has no <c>max</c>.
    /// </summary>
    public const string Effort = "docqams.effort";

    /// <summary>
    /// On <see cref="ChatOptions.AdditionalProperties"/>: the time to live (<c>5m</c> or <c>1h</c>) of an
    /// automatic cache breakpoint at the end of the conversation.
    /// </summary>
    public const string ConversationCacheTtl = "docqams.conversation_cache_ttl";

    // ---- Request side: AIContent.AdditionalProperties ---------------------------------------------------

    /// <summary>
    /// On a request <see cref="AIContent"/> (a <see cref="CitableDocumentContent"/> or a user
    /// <see cref="TextContent"/>): the time to live (<c>5m</c> or <c>1h</c>) of a cache breakpoint placed
    /// on that content, so the prompt up to and including it is cached.
    /// </summary>
    public const string CacheTtl = "docqams.cache_ttl";

    // ---- Both sides: AIContent.AdditionalProperties -----------------------------------------------------

    /// <summary>
    /// <see langword="true"/> on a <see cref="TextReasoningContent"/> whose reasoning the provider redacted:
    /// its <see cref="TextReasoningContent.ProtectedData"/> is opaque data that must be replayed unchanged,
    /// and it has no text.
    /// </summary>
    public const string RedactedReasoning = "docqams.redacted_reasoning";

    // ---- Response side: AIContent.AdditionalProperties --------------------------------------------------

    /// <summary>
    /// On a response <see cref="TextContent"/> or <see cref="TextReasoningContent"/>, streamed or final: the
    /// zero-based index of the content block it belongs to. Streamed deltas of one block share an index;
    /// a block is complete once content with a higher index, or the end of the stream, arrives.
    /// </summary>
    public const string BlockIndex = "docqams.block_index";

    // ---- Both sides: CitationAnnotation.AdditionalProperties -------------------------------------------

    /// <summary>
    /// On a <see cref="CitationAnnotation"/>: the provider's citation type. The app sends plain-text
    /// documents, which are cited by <c>char_location</c>; any other type is not checkable.
    /// </summary>
    public const string CitationType = "docqams.citation_type";

    /// <summary>On a <see cref="CitationAnnotation"/>: the zero-based index of the cited document in the request.</summary>
    public const string DocumentIndex = "docqams.document_index";

    /// <summary>
    /// On a <see cref="CitationAnnotation"/>: where the cited span starts in the document text, in code
    /// points (not UTF-16 units).
    /// </summary>
    public const string StartCharIndex = "docqams.start_char_index";

    /// <summary>On a <see cref="CitationAnnotation"/>: where the cited span ends (exclusive), in code points.</summary>
    public const string EndCharIndex = "docqams.end_char_index";

    // ---- Response side: ChatResponseUpdate / ChatResponse.AdditionalProperties -------------------------

    /// <summary>
    /// On the update that carries the <see cref="ChatFinishReason"/>, and on the final
    /// <see cref="ChatResponse"/>: the provider's raw stop reason (for example <c>end_turn</c>,
    /// <c>max_tokens</c> or <c>refusal</c>), which the finish reason summarizes.
    /// </summary>
    public const string StopReason = "docqams.stop_reason";

    /// <summary>With <see cref="StopReason"/> <c>refusal</c>: the refusal's category, when the provider gives one.</summary>
    public const string RefusalCategory = "docqams.refusal_category";

    /// <summary>With <see cref="StopReason"/> <c>refusal</c>: the provider's explanation, when it gives one.</summary>
    public const string RefusalExplanation = "docqams.refusal_explanation";

    // ---- Response side: UsageDetails.AdditionalCounts ---------------------------------------------------

    /// <summary>
    /// In <see cref="UsageDetails.AdditionalCounts"/>: input tokens written to the cache with a 1-hour time
    /// to live.
    /// </summary>
    /// <remarks>
    /// Usage follows the Microsoft.Extensions.AI convention: <see cref="UsageDetails.InputTokenCount"/> is
    /// every input token (uncached, cache reads and cache writes), <see cref="UsageDetails.CachedInputTokenCount"/>
    /// is the cache reads, and this key and <see cref="CacheWrite5mTokens"/> split the cache writes. The
    /// provider streams a <see cref="UsageContent"/> as soon as the input side is known (output zero), and a
    /// final one with the complete counts.
    /// </remarks>
    public const string CacheWrite1hTokens = "docqams.cache_write_1h_input_tokens";

    /// <summary>
    /// In <see cref="UsageDetails.AdditionalCounts"/>: input tokens written to the cache with a 5-minute time
    /// to live. See <see cref="CacheWrite1hTokens"/>.
    /// </summary>
    public const string CacheWrite5mTokens = "docqams.cache_write_5m_input_tokens";
}
