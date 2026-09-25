using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace DocQaMs.Core.Tests.Conversations;

/// <summary>Plan items 6 and 11: the cache warmth tracker, on a fake clock.</summary>
public sealed class CacheWarmthTrackerTests
{
    private static readonly CorpusCacheKey OpusHigh = new("fingerprint-1", ModelIds.ClaudeOpus5, EffortLevel.High);

    private static readonly TokenUsage ColdWrite = new(12, 200_000, 20, 0, 0);

    private static readonly TokenUsage WarmRead = new(12, 0, 20, 200_000, 0);

    private readonly FakeTimeProvider _time = new(StoreHarness.Start);

    private readonly CacheWarmthTracker _tracker;

    public CacheWarmthTrackerTests() => _tracker = new CacheWarmthTracker(_time);

    [Fact]
    public void AnEntryNeverUsed_IsCold() =>
        Assert.Equal(new CacheWarmth(CacheWarmthStatus.Cold, null), _tracker.GetWarmth(OpusHigh));

    [Fact]
    public void AnEntry_IsWarm54MinutesAfterItsLastUse_AndCold56MinutesAfter()
    {
        _tracker.RecordUsage(OpusHigh, ColdWrite);

        _time.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal(
            new CacheWarmth(CacheWarmthStatus.ProbablyWarm, StoreHarness.Start + TimeSpan.FromMinutes(55)),
            _tracker.GetWarmth(OpusHigh));

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CacheWarmthStatus.Cold, _tracker.GetWarmth(OpusHigh).Status);
    }

    [Fact]
    public void ARead_RefreshesTheTtl()
    {
        _tracker.RecordUsage(OpusHigh, ColdWrite);
        _time.Advance(TimeSpan.FromMinutes(50));
        _tracker.RecordUsage(OpusHigh, WarmRead);

        _time.Advance(TimeSpan.FromMinutes(50));

        CacheWarmth warmth = _tracker.GetWarmth(OpusHigh);
        Assert.True(warmth.IsProbablyWarm);
        Assert.Equal(StoreHarness.Start + TimeSpan.FromMinutes(50 + 55), warmth.WarmUntil);
    }

    [Fact]
    public void UsageWithNoCacheActivity_DoesNotWarmTheEntry()
    {
        _tracker.RecordUsage(OpusHigh, new TokenUsage(200_000, 0, 0, 0, 0));

        Assert.Equal(CacheWarmthStatus.Cold, _tracker.GetWarmth(OpusHigh).Status);
    }

    [Fact]
    public void Entries_AreKeyedByFingerprintModelAndLevel()
    {
        _tracker.RecordUsage(OpusHigh, ColdWrite);

        Assert.True(_tracker.GetWarmth(OpusHigh).IsProbablyWarm);
        Assert.False(_tracker.GetWarmth(OpusHigh with { Effort = EffortLevel.Low }).IsProbablyWarm);
        Assert.False(_tracker.GetWarmth(OpusHigh with { ModelId = ModelIds.ClaudeSonnet5 }).IsProbablyWarm);
        Assert.False(_tracker.GetWarmth(OpusHigh with { Fingerprint = "fingerprint-2" }).IsProbablyWarm);
    }

    [Fact]
    public void ARequestOnAColdEntry_MarksAWriteInProgress_UntilItsUsageArrives()
    {
        CacheWarmthTracker.CacheRequest request = _tracker.BeginRequest(OpusHigh);

        Assert.True(request.PredictedCold);
        Assert.Equal(new CacheWarmth(CacheWarmthStatus.ColdWriteInProgress, null), _tracker.GetWarmth(OpusHigh));

        _time.Advance(TimeSpan.FromSeconds(8));
        request.Started(ColdWrite);

        Assert.Equal(
            new CacheWarmth(CacheWarmthStatus.ProbablyWarm, StoreHarness.Start + TimeSpan.FromSeconds(8) + TimeSpan.FromMinutes(55)),
            _tracker.GetWarmth(OpusHigh));
    }

    [Fact]
    public void AFailedRequest_ClearsItsMark_OnceOnly()
    {
        CacheWarmthTracker.CacheRequest first = _tracker.BeginRequest(OpusHigh);
        CacheWarmthTracker.CacheRequest second = _tracker.BeginRequest(OpusHigh);
        Assert.True(second.PredictedCold);

        first.End();
        first.End();

        // The second request is still writing, so the mark stays.
        Assert.Equal(CacheWarmthStatus.ColdWriteInProgress, _tracker.GetWarmth(OpusHigh).Status);

        second.End();
        Assert.Equal(CacheWarmthStatus.Cold, _tracker.GetWarmth(OpusHigh).Status);
    }

    [Fact]
    public void ARequestOnAWarmEntry_IsNotPredictedCold_AndMarksNothing()
    {
        _tracker.RecordUsage(OpusHigh, ColdWrite);

        CacheWarmthTracker.CacheRequest request = _tracker.BeginRequest(OpusHigh);
        request.End();

        Assert.False(request.PredictedCold);
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(CacheWarmthStatus.Cold, _tracker.GetWarmth(OpusHigh).Status);
    }
}
