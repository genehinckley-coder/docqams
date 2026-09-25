using Anthropic.Models.Messages;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Streams one answer: a single request, and the events of its response as they arrive.
/// </summary>
/// <remarks>
/// Kept separate from conversation state, as the prototype kept it, so each can be tested alone. It
/// neither reads nor changes a conversation; the caller decides what to commit.
/// </remarks>
public interface IAnswerService
{
    /// <summary>
    /// Sends <paramref name="messages"/> and streams the response.
    /// </summary>
    /// <param name="settings">The model and level to answer at.</param>
    /// <param name="messages">
    /// The conversation so far, ending in the new user message. <c>messages[0]</c> holds the corpus
    /// document blocks and the first question. The list is not changed.
    /// </param>
    /// <param name="cancellationToken">Stop: cancelling it ends the answer and closes the HTTP stream.</param>
    /// <returns>
    /// The events in order: <see cref="AnswerEvent.UsageStarted"/> first, and
    /// <see cref="AnswerEvent.Completed"/> last when the response finished. Once the response has finished,
    /// <see cref="AnswerEvent.Completed"/> is sent even if <paramref name="cancellationToken"/> is cancelled
    /// after that: a finished answer has been paid for, so it is kept (plan item 6).
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. No <see cref="AnswerEvent.Completed"/> is sent,
    /// and a stopped answer's output tokens are never reported.
    /// </exception>
    /// <exception cref="AnswerFailedException">The answer failed, or timed out.</exception>
    /// <exception cref="ArgumentException"><paramref name="messages"/> is not a valid conversation.</exception>
    IAsyncEnumerable<AnswerEvent> StreamAsync(
        AnswerSettings settings,
        IReadOnlyList<MessageParam> messages,
        CancellationToken cancellationToken = default);
}
