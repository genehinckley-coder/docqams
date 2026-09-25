using DocQaMs.Core.Answering;
using DocQaMs.Core.Quotes;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Conversations;

/// <summary>How a turn ended.</summary>
public enum TurnOutcome
{
    /// <summary>The answer completed and was added to the conversation.</summary>
    Completed,

    /// <summary>Stop ended the answer. Nothing was added to the conversation.</summary>
    Stopped,

    /// <summary>The answer failed. Nothing was added to the conversation.</summary>
    Failed,

    /// <summary>
    /// The answer completed, but carried nothing that can be sent back, such as a refusal with no text.
    /// Nothing was added to the conversation: committing it would make every later request a 400.
    /// </summary>
    NotReplayable,
}

/// <summary>What a turn warning is about; each kind has its own badge.</summary>
public enum TurnWarningKind
{
    /// <summary>The answer reached its output limit.</summary>
    Truncated,

    /// <summary>The model declined to answer.</summary>
    Refused,

    /// <summary>The answer ended for another reason than <c>end_turn</c>.</summary>
    Incomplete,

    /// <summary>The turn read less of the corpus cache than it should have.</summary>
    CacheMiss,

    /// <summary>The response reports no cache activity at all.</summary>
    NotCached,

    /// <summary>The request filled more of the context window than the warning threshold.</summary>
    Context,
}

/// <summary>One warning about a completed turn.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Message">The warning as the UI shows it, starting with its badge text in capitals.</param>
public sealed record TurnWarning(TurnWarningKind Kind, string Message);

/// <summary>
/// One text block of an answer, as it streamed, with the source numbers its <c>[n]</c> markers show.
/// </summary>
/// <param name="Text">The block's text.</param>
/// <param name="SourceNumbers">
/// The distinct sources the block cites, in order; empty until the block completes, and for an uncited block.
/// Number <c>n</c> is <see cref="AnswerResult.Sources"/><c>[n - 1]</c>.
/// </param>
public sealed record AnswerSegment(string Text, IReadOnlyList<int> SourceNumbers);

/// <summary>
/// One question and what came of it: a committed answer, or a stopped, failed or unreplayable one that stays
/// visible without being added to the conversation.
/// </summary>
public sealed record AnswerRecord
{
    /// <summary>The question, trimmed.</summary>
    public required string Question { get; init; }

    /// <summary>The model and level it was asked at.</summary>
    public required AnswerSettings Settings { get; init; }

    /// <summary>
    /// The corpus snapshot its request was built from. Citations index into it, never into whichever corpus is
    /// current after a reload.
    /// </summary>
    public required CorpusSnapshot Corpus { get; init; }

    /// <summary>When it was asked.</summary>
    public required DateTimeOffset AskedAt { get; init; }

    /// <summary>How the turn ended.</summary>
    public required TurnOutcome Outcome { get; init; }

    /// <summary>The answer's text blocks as they streamed; for a stopped or failed turn, the text so far.</summary>
    public required IReadOnlyList<AnswerSegment> Segments { get; init; }

    /// <summary>The thinking summary, as it streamed; empty when there was none.</summary>
    public required string Thinking { get; init; }

    /// <summary>Whether a redacted thinking block arrived, whose content cannot be shown.</summary>
    public required bool HasRedactedThinking { get; init; }

    /// <summary>The completed response, or <see langword="null"/> for a stopped or failed turn.</summary>
    public AnswerResult? Result { get; init; }

    /// <summary>The quote check of a completed response; <see langword="null"/> for a stopped or failed one, which is not checked.</summary>
    public QuoteReport? Quotes { get; init; }

    /// <summary>How a completed response used the corpus cache; <see langword="null"/> for a stopped or failed one.</summary>
    public CacheReport? Cache { get; init; }

    /// <summary>The warnings about a completed response, in the order they are shown.</summary>
    public IReadOnlyList<TurnWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Why the turn was not added to the conversation, as the UI shows it; <see langword="null"/> for a committed
    /// answer.
    /// </summary>
    public string? Notice { get; init; }

    /// <summary>What kind of failure ended a failed turn; otherwise <see langword="null"/>.</summary>
    public AnswerFailureKind? FailureKind { get; init; }

    /// <summary>
    /// What the turn is known to have cost: the whole response when it completed, and only the input side when
    /// it was stopped or failed after its usage arrived. <see langword="null"/> when the cost is unknown.
    /// </summary>
    public TurnCost? Cost { get; init; }

    /// <summary>What <see cref="Cost"/> leaves out, as the UI shows it; <see langword="null"/> for a completed response.</summary>
    public string? CostNote { get; init; }

    /// <summary>Whether the answer was added to the conversation.</summary>
    public bool IsCommitted => Outcome == TurnOutcome.Completed;
}

/// <summary>
/// The answer in flight, as far as it has streamed. Part of the snapshot, so a tab opened or refreshed
/// mid-answer shows it, and the Stop button.
/// </summary>
public sealed record InProgressAnswer
{
    /// <summary>The question, trimmed.</summary>
    public required string Question { get; init; }

    /// <summary>The model and level it is being answered at.</summary>
    public required AnswerSettings Settings { get; init; }

    /// <summary>When it was asked.</summary>
    public required DateTimeOffset AskedAt { get; init; }

    /// <summary>The text blocks so far.</summary>
    public required IReadOnlyList<AnswerSegment> Segments { get; init; }

    /// <summary>The thinking summary so far.</summary>
    public required string Thinking { get; init; }

    /// <summary>Whether a redacted thinking block has arrived.</summary>
    public required bool HasRedactedThinking { get; init; }

    /// <summary>The input side of the usage, once the server has reported it; <see langword="null"/> before.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>Whether Stop has been requested and the outcome is not yet published.</summary>
    public required bool IsStopping { get; init; }

    /// <summary>Whether any answer text has arrived yet.</summary>
    public bool HasText => Segments.Any(segment => segment.Text.Length > 0);
}
