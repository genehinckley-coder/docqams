using DocQaMs.Core.Answering;
using Microsoft.Extensions.AI;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Conversations;

/// <summary>
/// What is known to have been spent, and what is not: a stopped turn's output is never reported, and a turn
/// stopped before its usage arrived has an unknown cost.
/// </summary>
/// <param name="Known">The cost that is known, per bucket.</param>
/// <param name="UnknownCostTurns">Turns stopped or failed before the server reported their usage.</param>
/// <param name="UnreportedOutputTurns">Turns stopped or failed after their usage arrived, whose output is not reported.</param>
public sealed record ConversationCost(TurnCost Known, int UnknownCostTurns, int UnreportedOutputTurns)
{
    /// <summary>Nothing spent.</summary>
    public static ConversationCost Zero { get; } = new(TurnCost.Zero, 0, 0);

    /// <summary>Whether <see cref="Known"/> is the whole cost.</summary>
    public bool IsComplete => UnknownCostTurns == 0 && UnreportedOutputTurns == 0;

    /// <summary>Adds two totals.</summary>
    /// <param name="other">The total to add.</param>
    /// <returns>The sum.</returns>
    public ConversationCost Add(ConversationCost other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new ConversationCost(
            Known.Add(other.Known),
            UnknownCostTurns + other.UnknownCostTurns,
            UnreportedOutputTurns + other.UnreportedOutputTurns);
    }
}

/// <summary>
/// An immutable view of one conversation, as the UI renders it. The store hands these out and never its live
/// state.
/// </summary>
public sealed record ConversationSnapshot
{
    /// <summary>The conversation's id.</summary>
    public required Guid Id { get; init; }

    /// <summary>A short title: the first committed question, or the question being asked, shortened.</summary>
    public required string Title { get; init; }

    /// <summary>When the conversation was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The model and level the next question is asked at.</summary>
    public required AnswerSettings Settings { get; init; }

    /// <summary>
    /// Whether <see cref="Settings"/> can no longer change: a turn is in flight, or at least one is committed.
    /// Changing them then means a new conversation.
    /// </summary>
    public required bool SettingsLocked { get; init; }

    /// <summary>
    /// The corpus snapshot the committed turns were built from, or <see langword="null"/> until a turn is
    /// committed. Until then, each question uses the corpus current when it is asked.
    /// </summary>
    public required CorpusSnapshot? Corpus { get; init; }

    /// <summary>The committed conversation, alternating user and assistant, as the next request resends it.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Every finished question in order, committed or not.</summary>
    public required IReadOnlyList<AnswerRecord> Answers { get; init; }

    /// <summary>The answer in flight, or <see langword="null"/> when none is.</summary>
    public required InProgressAnswer? InProgress { get; init; }

    /// <summary>How the most recent turn ended, or <see langword="null"/> before any turn has.</summary>
    public required TurnOutcome? LastOutcome { get; init; }

    /// <summary>
    /// A question returned to the input by a stopped, failed or unreplayable turn, until the input takes it with
    /// <see cref="IConversationStore.TakeReturnedQuestion"/>.
    /// </summary>
    public required string? ReturnedQuestion { get; init; }

    /// <summary>What the conversation has cost, stopped and failed turns included.</summary>
    public required ConversationCost Cost { get; init; }

    /// <summary>
    /// The size of the latest completed request in the context window (input, cache writes and cache reads), or
    /// <see langword="null"/> before one completes. Not cumulative: every turn resends the whole conversation.
    /// </summary>
    public required long? LastRequestTokens { get; init; }

    /// <summary>Whether an answer is in flight.</summary>
    public bool IsAnswering => InProgress is not null;

    /// <summary>The number of committed answers.</summary>
    public int CommittedAnswers => Messages.Count / 2;
}

/// <summary>What became of a question handed to <see cref="IConversationStore.Ask"/>.</summary>
public sealed record AskResult
{
    private AskResult(string? refusal, Task turn)
    {
        Refusal = refusal;
        Turn = turn;
    }

    /// <summary>Whether the question was sent.</summary>
    public bool Accepted => Refusal is null;

    /// <summary>Why the question was not sent, for the user; <see langword="null"/> when it was.</summary>
    public string? Refusal { get; }

    /// <summary>
    /// Completes when the turn's outcome is published; already complete when the question was refused. It
    /// never faults. The UI need not wait for it: the store's change events carry the progress.
    /// </summary>
    public Task Turn { get; }

    internal static AskResult Started(Task turn) => new(null, turn);

    internal static AskResult Refused(string reason) => new(reason, Task.CompletedTask);
}
