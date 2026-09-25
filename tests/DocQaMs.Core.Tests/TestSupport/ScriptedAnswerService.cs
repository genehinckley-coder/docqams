using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// An answer service whose every answer a test drives by hand, one event at a time, so it can act between any
/// two of them.
/// </summary>
internal sealed class ScriptedAnswerService : IAnswerService
{
    private readonly Channel<ScriptedAnswer> _reading = Channel.CreateUnbounded<ScriptedAnswer>();
    private readonly List<ScriptedAnswer> _calls = [];

    /// <summary>Every answer requested so far, in order.</summary>
    public IReadOnlyList<ScriptedAnswer> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>When set, each new answer ignores its token, as a stream stalled in a read that has not noticed Stop.</summary>
    public bool IgnoreCancellation { get; set; }

    public IAsyncEnumerable<AnswerEvent> StreamAsync(
        AnswerSettings settings, IReadOnlyList<MessageParam> messages, CancellationToken cancellationToken = default)
    {
        var answer = new ScriptedAnswer(settings, [.. messages], IgnoreCancellation, started => _reading.Writer.TryWrite(started));
        lock (_calls)
        {
            _calls.Add(answer);
        }

        return answer.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// The next answer the store starts reading, with its token set. Fails the test if none starts within 10
    /// seconds.
    /// </summary>
    public async Task<ScriptedAnswer> NextAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _reading.Reader.ReadAsync(timeout.Token);
    }
}

/// <summary>One answer in progress: the request it was given, and the events a test sends it.</summary>
internal sealed class ScriptedAnswer(
    AnswerSettings settings, IReadOnlyList<MessageParam> messages, bool ignoreCancellation, Action<ScriptedAnswer> onReading)
{
    private readonly Channel<object> _items = Channel.CreateUnbounded<object>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AnswerSettings Settings => settings;

    /// <summary>The request's messages: the committed conversation plus the new question.</summary>
    public IReadOnlyList<MessageParam> Messages => messages;

    /// <summary>The token the store passed; cancelled by Stop.</summary>
    public CancellationToken Token { get; private set; }

    /// <summary>Completes when the store has closed the stream.</summary>
    public Task Closed => _closed.Task;

    /// <summary>The usage event that starts <paramref name="result"/>: its input side, priced.</summary>
    public static AnswerEvent.UsageStarted UsageOf(AnswerResult result) =>
        new(result.Usage with { Output = 0 }, result.Cost with { Output = 0m });

    public void Send(params AnswerEvent[] events)
    {
        foreach (AnswerEvent answerEvent in events)
        {
            _items.Writer.TryWrite(answerEvent);
        }
    }

    /// <summary>Makes the stream throw <paramref name="exception"/> once the events before it are read.</summary>
    public void Fail(Exception exception) => _items.Writer.TryWrite(exception);

    /// <summary>Ends the stream without a final event.</summary>
    public void End() => _items.Writer.TryComplete();

    /// <summary>Sends a whole answer: its usage, one text block, and the final event.</summary>
    public void Answer(AnswerResult result) => Send(
        UsageOf(result),
        new AnswerEvent.TextDelta(0, "An answer."),
        new AnswerEvent.TextBlockCompleted(0, [], []),
        new AnswerEvent.Completed(result));

    internal async IAsyncEnumerable<AnswerEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Token = cancellationToken;
        onReading(this);
        try
        {
            // Events already sent are delivered even after cancellation, as the real service still delivers a
            // response that has finished.
            while (await _items.Reader.WaitToReadAsync(ignoreCancellation ? CancellationToken.None : cancellationToken))
            {
                while (_items.Reader.TryRead(out object? item))
                {
                    if (item is Exception exception)
                    {
                        throw exception;
                    }

                    yield return (AnswerEvent)item;
                }
            }
        }
        finally
        {
            _closed.TrySetResult();
        }
    }
}
