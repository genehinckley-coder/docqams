namespace DocQaMs.Web;

/// <summary>
/// Coalesces render requests to at most one per interval. The store raises a change for every streamed fragment, and
/// a 6,000-token answer has thousands of them; rendering each would send as many batches over SignalR.
/// </summary>
/// <remarks>
/// <see cref="Request"/> renders at once when the last render is at least an interval old, and otherwise schedules
/// one render for when the interval ends, however many requests arrive meanwhile. <see cref="Flush"/> renders at once
/// and cancels the scheduled one: for a change that must never wait or be dropped, such as an answer's end. Both are
/// safe to call from any thread; the render callback marshals itself onto the component's dispatcher.
/// <para>
/// The callback runs outside the throttle's lock, so a render already decided on can still run once after
/// <see cref="Dispose"/> returns, when the two race. The callback must therefore check its component's own disposal,
/// as the Conversation page's does.
/// </para>
/// </remarks>
internal sealed class RenderThrottle : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Action _render;
    private readonly Lock _gate = new();
    private readonly ITimer _timer;
    private DateTimeOffset _lastRender = DateTimeOffset.MinValue;
    private bool _pending;
    private bool _disposed;

    /// <summary>Creates the throttle.</summary>
    /// <param name="time">The clock and timer.</param>
    /// <param name="interval">The shortest time between two renders that <see cref="Request"/> causes.</param>
    /// <param name="render">Renders; called on whichever thread asked, or on the timer's.</param>
    public RenderThrottle(TimeProvider time, TimeSpan interval, Action render)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(render);

        _time = time;
        _interval = interval;
        _render = render;
        _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Renders now, or once when the interval since the last render ends.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_disposed || _pending)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            TimeSpan wait = _lastRender + _interval - now;
            if (wait > TimeSpan.Zero)
            {
                _pending = true;
                _timer.Change(wait, Timeout.InfiniteTimeSpan);
                return;
            }

            _lastRender = now;
        }

        _render();
    }

    /// <summary>Renders now, and cancels any render already scheduled.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _lastRender = _time.GetUtcNow();
        }

        _render();
    }

    /// <summary>Stops rendering; a scheduled render is dropped.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            // A flush since the render was scheduled has rendered already.
            if (_disposed || !_pending)
            {
                return;
            }

            _pending = false;
            _lastRender = _time.GetUtcNow();
        }

        _render();
    }
}
