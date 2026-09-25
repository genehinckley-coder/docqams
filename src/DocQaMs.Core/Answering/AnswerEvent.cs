using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>
/// One thing that happened while an answer streamed, in the order it happened.
/// </summary>
/// <remarks>
/// A stream always starts with <see cref="UsageStarted"/>, and it ends with <see cref="Completed"/> only
/// when the response finished. A stopped or failed answer ends with an exception instead.
/// </remarks>
public abstract record AnswerEvent
{
    private AnswerEvent()
    {
    }

    /// <summary>
    /// The input side of the usage, from the first <see cref="UsageContent"/>, as soon as it arrives. It is what a stopped
    /// turn is known to have cost: output is reported only when the response ends.
    /// </summary>
    /// <param name="Usage">The input, cache write and cache read tokens. Output is zero.</param>
    /// <param name="Cost">That usage priced at the selected model's prices.</param>
    public sealed record UsageStarted(TokenUsage Usage, TurnCost Cost) : AnswerEvent;

    /// <summary>A fragment of the thinking summary.</summary>
    /// <param name="Text">The fragment.</param>
    public sealed record ThinkingDelta(string Text) : AnswerEvent;

    /// <summary>A redacted thinking block arrived. Its content is encrypted and cannot be shown.</summary>
    public sealed record ThinkingRedacted : AnswerEvent;

    /// <summary>A fragment of the answer text.</summary>
    /// <param name="BlockIndex">The index of the content block it belongs to.</param>
    /// <param name="Text">The fragment.</param>
    public sealed record TextDelta(int BlockIndex, string Text) : AnswerEvent;

    /// <summary>
    /// A text block ended. Its <c>[n]</c> markers go after it: a citation can arrive before the text it
    /// cites, and it applies to the whole block anyway.
    /// </summary>
    /// <param name="BlockIndex">The index of the content block.</param>
    /// <param name="Citations">The block's citations, in the order they arrived; empty for an uncited block.</param>
    /// <param name="SourceNumbers">
    /// The source number of each distinct span the block cites, once each, in order. Number <c>n</c> is
    /// <c>Sources[n - 1]</c> of the completed answer.
    /// </param>
    public sealed record TextBlockCompleted(
        int BlockIndex,
        IReadOnlyList<AnswerCitation> Citations,
        IReadOnlyList<int> SourceNumbers) : AnswerEvent;

    /// <summary>The response finished.</summary>
    /// <param name="Result">The final response and what was learned from it.</param>
    public sealed record Completed(AnswerResult Result) : AnswerEvent;
}

/// <summary>
/// A finished response: the final reply, its sources, usage and cost, and how it ended.
/// </summary>
/// <param name="Settings">The model and level it was requested at.</param>
/// <param name="Response">
/// The final response: one assistant message holding one content per content block, in block order, with
/// citations and thinking signatures intact (see <see cref="Reply"/>); the finish reason, the usage, and the
/// provider's stop reason and refusal details.
/// </param>
/// <param name="Sources">
/// The distinct cited spans in the order first cited. Marker <c>[n]</c> is <c>Sources[n - 1]</c>.
/// </param>
/// <param name="Usage">The complete usage, output included.</param>
/// <param name="Cost">The usage priced at the selected model's prices.</param>
/// <param name="Elapsed">From sending the request to the final event.</param>
/// <param name="StopWarning">Why the answer cannot be taken as complete, or <see langword="null"/> when it ended normally.</param>
public sealed record AnswerResult(
    AnswerSettings Settings,
    ChatResponse Response,
    IReadOnlyList<AnswerCitation> Sources,
    TokenUsage Usage,
    TurnCost Cost,
    TimeSpan Elapsed,
    StopWarning? StopWarning)
{
    /// <summary>The warning shown when a response reports no cache activity at all.</summary>
    public const string NotCachedWarning =
        "NOT CACHED: the response reports no cache write and no cache read, so the corpus breakpoint did " +
        "not take effect and every question pays the full input price.";

    /// <summary>
    /// The assistant message of <see cref="Response"/>, or an empty assistant message when it has none.
    /// </summary>
    public ChatMessage Reply =>
        Response.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant) ?? new ChatMessage(ChatRole.Assistant, []);

    /// <summary><see cref="NotCachedWarning"/> when the response shows no cache activity; otherwise <see langword="null"/>.</summary>
    public string? CacheWarning => Usage.HasCacheActivity ? null : NotCachedWarning;
}
