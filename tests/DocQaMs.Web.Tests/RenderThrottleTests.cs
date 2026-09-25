using Microsoft.Extensions.Time.Testing;

namespace DocQaMs.Web.Tests;

/// <summary>Plan item 8: renders are throttled to about every 100 ms, and the end of an answer is never dropped.</summary>
public sealed class RenderThrottleTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly FakeTimeProvider _time = new();
    private readonly RenderThrottle _throttle;
    private int _renders;

    public RenderThrottleTests() => _throttle = new RenderThrottle(_time, Interval, () => Interlocked.Increment(ref _renders));

    public void Dispose() => _throttle.Dispose();

    [Fact]
    public void TheFirstRequest_RendersAtOnce()
    {
        _throttle.Request();

        Assert.Equal(1, _renders);
    }

    [Fact]
    public void RequestsWithinTheInterval_BecomeOneRender_WhenItEnds()
    {
        _throttle.Request();
        for (int i = 0; i < 50; i++)
        {
            _throttle.Request();
        }

        Assert.Equal(1, _renders);

        _time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, _renders);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, _renders);

        // Nothing more was asked for.
        _time.Advance(Interval * 5);
        Assert.Equal(2, _renders);
    }

    [Fact]
    public void ARequestAfterTheInterval_RendersAtOnce()
    {
        _throttle.Request();
        _time.Advance(Interval);

        _throttle.Request();

        Assert.Equal(2, _renders);
    }

    [Fact]
    public void Flush_RendersAtOnce_AndReplacesTheScheduledRender()
    {
        _throttle.Request();
        _throttle.Request();

        _throttle.Flush();
        Assert.Equal(2, _renders);

        _time.Advance(Interval);
        Assert.Equal(2, _renders);
    }

    [Fact]
    public void Flush_RendersEvenWithinTheInterval()
    {
        _throttle.Flush();
        _throttle.Flush();

        Assert.Equal(2, _renders);
    }

    [Fact]
    public void AfterDispose_NothingRenders()
    {
        _throttle.Request();
        _throttle.Request();

        _throttle.Dispose();
        _time.Advance(Interval);
        _throttle.Request();
        _throttle.Flush();

        Assert.Equal(1, _renders);
    }
}
