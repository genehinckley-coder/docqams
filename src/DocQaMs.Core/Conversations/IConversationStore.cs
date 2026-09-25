using DocQaMs.Core.Answering;

namespace DocQaMs.Core.Conversations;

/// <summary>
/// The app's conversations, shared by every tab for the app's lifetime. Each conversation's turns run here,
/// not in a page, so refreshing or closing a tab neither stops an answer nor throws it away.
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// Raised after a conversation changes, with its id, on whichever thread changed it. Handlers call
    /// <see cref="Get"/>, which may already be newer than the change that raised the notification. Raised
    /// outside the store's locks; a handler that throws is logged and affects nothing else.
    /// </summary>
    event Action<Guid>? Changed;

    /// <summary>What every conversation has cost, stopped and failed turns included.</summary>
    ConversationCost TotalCost { get; }

    /// <summary>Every conversation, newest first.</summary>
    /// <returns>A snapshot of each.</returns>
    IReadOnlyList<ConversationSnapshot> List();

    /// <summary>One conversation.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Its snapshot, or <see langword="null"/> when there is no such conversation.</returns>
    ConversationSnapshot? Get(Guid id);

    /// <summary>
    /// Starts a new, empty conversation. Its first question builds <c>messages[0]</c> afresh from the corpus,
    /// which reads the corpus cache when the model and level have been used within the hour.
    /// </summary>
    /// <param name="settings">
    /// Its model and level: the active conversation's for "New conversation", or new ones for "New conversation
    /// with different settings". <see langword="null"/> on first run means <see cref="AnswerSettings.Default"/>.
    /// </param>
    /// <returns>Its snapshot.</returns>
    ConversationSnapshot Create(AnswerSettings? settings = null);

    /// <summary>
    /// Changes a conversation's model and level, while it has nothing to protect: no turn in flight and none
    /// committed.
    /// </summary>
    /// <param name="id">The conversation.</param>
    /// <param name="settings">The new settings.</param>
    /// <returns>Whether they changed; <see langword="false"/> when the settings are locked or there is no such conversation.</returns>
    bool TryChangeSettings(Guid id, AnswerSettings settings);

    /// <summary>
    /// Sends a question, and returns at once; the answer streams into the conversation's snapshot.
    /// </summary>
    /// <remarks>
    /// The question and its reply are committed only after the answer completes, and only if the reply can be
    /// replayed. Any failure, and Stop, leave the committed conversation exactly as it was, and return the
    /// question through <see cref="ConversationSnapshot.ReturnedQuestion"/>.
    /// </remarks>
    /// <param name="id">The conversation.</param>
    /// <param name="question">The question.</param>
    /// <returns>
    /// Whether it was sent. It is refused when it is empty, the conversation does not exist or already has an
    /// answer in flight, an empty conversation has no corpus to ask about, or a reload has replaced the corpus
    /// the conversation's committed turns were built from; that conversation stays readable, and a new one asks
    /// about the current corpus.
    /// </returns>
    AskResult Ask(Guid id, string question);

    /// <summary>
    /// Ends the wait for a conversation's answer in flight. Returns at once, without waiting for the stream to
    /// close. A no-op when no answer is in flight, when Stop was already requested, and once the answer has
    /// completed: a finished answer has been paid for, so it is kept.
    /// </summary>
    /// <param name="id">The conversation.</param>
    void Stop(Guid id);

    /// <summary>
    /// Takes the question a stopped, failed or unreplayable turn returned to the input, clearing it so that it is
    /// restored only once.
    /// </summary>
    /// <param name="id">The conversation.</param>
    /// <returns>The question, or <see langword="null"/> when there is none.</returns>
    string? TakeReturnedQuestion(Guid id);
}
