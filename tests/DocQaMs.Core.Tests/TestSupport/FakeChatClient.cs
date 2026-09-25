using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// A chat client that streams a scripted response to every request and records each request, without any
/// provider or network.
/// </summary>
/// <param name="respond">Builds the response to the nth request, counting from 1.</param>
internal sealed class FakeChatClient(Func<int, FakeResponse> respond) : IChatClient
{
    private readonly List<ChatCall> _calls = [];

    /// <summary>Answers every request with <paramref name="updates"/>, then ends the stream normally.</summary>
    public FakeChatClient(IEnumerable<ChatResponseUpdate> updates)
        : this(new FakeResponse(updates))
    {
    }

    /// <summary>Answers every request with <paramref name="response"/>.</summary>
    public FakeChatClient(FakeResponse response)
        : this(_ => response)
    {
    }

    /// <summary>Every request so far, in order.</summary>
    public IReadOnlyList<ChatCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public bool Disposed { get; private set; }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatCall call;
        lock (_calls)
        {
            call = new ChatCall(_calls.Count + 1, [.. messages], options, cancellationToken);
            _calls.Add(call);
        }

        return new FakeStream(respond(call.Number), call);
    }

    /// <summary>The app only streams; a non-streaming call means it has changed.</summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The app streams every answer.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => Disposed = true;

    private sealed class FakeStream(FakeResponse response, ChatCall call) : IAsyncEnumerable<ChatResponseUpdate>
    {
        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(response, call);
    }

    /// <summary>Plays the response's steps, one update per read, as a provider's stream delivers them.</summary>
    private sealed class Enumerator(FakeResponse response, ChatCall call) : IAsyncEnumerator<ChatResponseUpdate>
    {
        private int _next;

        public ChatResponseUpdate Current { get; private set; } = null!;

        public async ValueTask<bool> MoveNextAsync()
        {
            // Every read observes the token, as an HTTP stream's does.
            call.Token.ThrowIfCancellationRequested();
            await Task.Yield();

            while (_next < response.Steps.Count)
            {
                object step = response.Steps[_next++];
                switch (step)
                {
                    case ChatResponseUpdate update:
                        Current = update;
                        return true;
                    case Exception exception:
                        throw exception;
                    case Action action:
                        action();
                        break;
                    case FakeResponse.StallStep:
                        await response.StallAsync(call.Token);
                        break;
                }
            }

            return false;
        }

        public ValueTask DisposeAsync()
        {
            call.MarkDisposed();
            return response.DisposeFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// One scripted response: updates in order, with optional actions, a failure or a stall between them. Build it
/// fluently; each enumeration plays it from the start.
/// </summary>
internal sealed class FakeResponse
{
    private readonly List<object> _steps = [];
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _stallEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeResponse(IEnumerable<ChatResponseUpdate> updates) => _steps.AddRange(updates);

    public IReadOnlyList<object> Steps => _steps;

    /// <summary>Thrown when the stream is disposed, after whatever it delivered.</summary>
    public Exception? DisposeFailure { get; private set; }

    /// <summary>Completes when a read starts to stall.</summary>
    public Task Stalled => _stalled.Task;

    /// <summary>How the stall ended: <c>cancelled</c>, or <c>cap</c> when nothing ended it (so a test can never hang).</summary>
    public Task<string> StallEnded => _stallEnded.Task;

    public FakeResponse Then(params IEnumerable<ChatResponseUpdate> updates)
    {
        _steps.AddRange(updates);
        return this;
    }

    /// <summary>Runs <paramref name="action"/> when the stream reaches this point, inside the read.</summary>
    public FakeResponse ThenRun(Action action)
    {
        _steps.Add(action);
        return this;
    }

    /// <summary>Makes the read that reaches this point throw <paramref name="exception"/>.</summary>
    public FakeResponse ThenThrow(Exception exception)
    {
        _steps.Add(exception);
        return this;
    }

    /// <summary>
    /// Makes the read that reaches this point wait until its token is cancelled, then throw
    /// <see cref="OperationCanceledException"/>; or, after 30 seconds, carry on.
    /// </summary>
    public FakeResponse ThenStall()
    {
        _steps.Add(new StallStep());
        return this;
    }

    public FakeResponse ThrowingOnDispose(Exception exception)
    {
        DisposeFailure = exception;
        return this;
    }

    internal async Task StallAsync(CancellationToken cancellationToken)
    {
        _stalled.TrySetResult();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            _stallEnded.TrySetResult("cap");
        }
        catch (OperationCanceledException)
        {
            _stallEnded.TrySetResult("cancelled");
            throw;
        }
    }

    internal sealed class StallStep;
}

/// <summary>One request to <see cref="FakeChatClient"/>: what it was sent, and what became of its stream.</summary>
/// <param name="Number">The request's number, counting from 1.</param>
/// <param name="Messages">The messages as sent.</param>
/// <param name="Options">The options as sent.</param>
/// <param name="Token">The token the request was made with.</param>
internal sealed record ChatCall(int Number, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, CancellationToken Token)
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the caller disposes the response stream.</summary>
    public Task Disposed => _disposed.Task;

    internal void MarkDisposed() => _disposed.TrySetResult();
}

/// <summary>A chat client provider that hands out one client for every level, and records the levels asked for.</summary>
internal sealed class FakeChatClientProvider(IChatClient client) : IChatClientProvider
{
    private readonly List<EffortLevel> _levels = [];

    public IChatClient Client => client;

    public IReadOnlyList<EffortLevel> Levels
    {
        get
        {
            lock (_levels)
            {
                return [.. _levels];
            }
        }
    }

    public IChatClient ForEffort(EffortLevel level)
    {
        if (!CacheAffecting.EffortLevels.Contains(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Not a selectable effort level.");
        }

        lock (_levels)
        {
            _levels.Add(level);
        }

        return client;
    }
}
