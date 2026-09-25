using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Tests.Conversations;

/// <summary>Plan items 6 and 11: the cache baseline, ported from the prototype's <c>Session.record</c>.</summary>
public sealed class CacheBaselinesTests
{
    private const long Prefix = 200_000;

    private static readonly CorpusCacheKey Key = new("fingerprint-1", ModelIds.ClaudeOpus5, EffortLevel.High);

    private static readonly DocQaOptions Settings = TestSettings.ValidOptions();

    private static readonly ModelPrices Opus = Settings.Prices[ModelIds.ClaudeOpus5];

    private readonly CacheBaselines _baselines = new(Options.Create(Settings));

    // Truncated, as the prototype's int() truncates.
    private static long Tolerance => (long)(Prefix * Settings.CacheReadToleranceFraction);

    [Fact]
    public void AFirstWrite_SetsTheBaseline_AndIsAColdWrite()
    {
        Assert.Equal(new CacheReport(CacheStatus.ColdWrite), Record(Usage(write1h: Prefix)));
        Assert.Equal(Prefix, _baselines.Get(Key));
    }

    [Fact]
    public void AFirstRead_SetsTheBaseline_AndIsARead()
    {
        Assert.Equal(new CacheReport(CacheStatus.Read), Record(Usage(read: Prefix)));
        Assert.Equal(Prefix, _baselines.Get(Key));
    }

    [Fact]
    public void AFirstTurnSplitBetweenAWriteAndARead_SetsTheBaselineToTheSum()
    {
        Record(Usage(write1h: 150_000, read: 50_000));

        Assert.Equal(Prefix, _baselines.Get(Key));
    }

    [Fact]
    public void AResponseWithNoCacheActivity_IsNotCached_AndSetsNoBaseline()
    {
        Assert.Equal(CacheStatus.NotCached, Record(Usage()).Status);
        Assert.Null(_baselines.Get(Key));
    }

    [Fact]
    public void LaterTurns_NeverMoveTheBaseline()
    {
        Record(Usage(write1h: Prefix));

        Assert.Equal(CacheStatus.Read, Record(Usage(read: Prefix + 5_000)).Status);
        Assert.Equal(Prefix, _baselines.Get(Key));
    }

    [Fact]
    public void AReadAtTheToleranceBoundary_IsARead_AndOneTokenLessIsAMiss()
    {
        Record(Usage(write1h: Prefix));

        Assert.Equal(CacheStatus.Read, Record(Usage(read: Prefix - Tolerance)).Status);
        Assert.Equal(CacheStatus.Miss, Record(Usage(read: Prefix - Tolerance - 1)).Status);
    }

    [Fact]
    public void AFirstTurnThatReads_ThenATurnThatReadsNothing_IsACacheMiss()
    {
        Assert.Equal(CacheStatus.Read, Record(Usage(read: Prefix)).Status);

        CacheReport report = Record(Usage());

        Assert.Equal(CacheStatus.Miss, report.Status);
        Assert.Equal(CacheMissKind.PrefixChanged, report.MissKind);
        Assert.Equal(
            "CACHE MISS: this turn read 0 cached tokens, short of the 200,000 the corpus prefix should give. The prefix no " +
            "longer matches, so every turn is paying full input price. Check whether the corpus or a cache-affecting value " +
            "changed; reloading the corpus reports what differs.",
            report.Warning);
    }

    [Fact]
    public void AFullWriteTheTrackerDidNotPredict_IsAnExpiredEntry_WithTheExtraCostNamed()
    {
        Record(Usage(write1h: Prefix));

        CacheReport report = Record(Usage(write1h: Prefix));

        decimal extra = Prefix * (Opus.CacheWrite1hPerMTok - Opus.CacheReadPerMTok) / 1_000_000m;
        Assert.Equal(CacheStatus.Miss, report.Status);
        Assert.Equal(CacheMissKind.Expired, report.MissKind);
        Assert.Equal(
            "CACHE MISS: this turn read 0 cached tokens, short of the 200,000 the corpus prefix should give. It wrote the " +
            "corpus afresh instead (200,000 tokens), so the 1-hour entry had expired or been evicted. " +
            FormattableString.Invariant($"That cost about ${extra:0.00} more than a read; the turns after it should read it again."),
            report.Warning);
    }

    [Fact]
    public void AFullWriteTheTrackerPredicted_IsAColdWrite_NotAMiss()
    {
        Record(Usage(write1h: Prefix));

        Assert.Equal(new CacheReport(CacheStatus.ColdWrite), Record(Usage(write1h: Prefix), predictedCold: true));
    }

    [Fact]
    public void APartialWrite_IsAMiss_EvenWhenACold_WriteWasPredicted()
    {
        Record(Usage(write1h: Prefix));

        CacheReport report = Record(Usage(write1h: Prefix / 2), predictedCold: true);

        Assert.Equal(CacheStatus.Miss, report.Status);
        Assert.Equal(CacheMissKind.PrefixChanged, report.MissKind);
    }

    [Fact]
    public void Baselines_AreHeldPerEntry()
    {
        Record(Usage(write1h: Prefix));
        CorpusCacheKey sonnet = Key with { ModelId = ModelIds.ClaudeSonnet5 };

        // Sonnet's first turn sets its own baseline rather than being measured against Opus's.
        Assert.Equal(CacheStatus.ColdWrite, _baselines.Record(sonnet, Usage(write1h: 100), predictedCold: false, Opus).Status);
        Assert.Equal(100, _baselines.Get(sonnet));
        Assert.Equal(Prefix, _baselines.Get(Key));
    }

    [Fact]
    public void DroppingAFingerprint_DropsOnlyItsBaselines()
    {
        CorpusCacheKey low = Key with { Effort = EffortLevel.Low };
        CorpusCacheKey other = Key with { Fingerprint = "fingerprint-2" };
        Record(Usage(write1h: Prefix));
        _baselines.Record(low, Usage(write1h: Prefix), predictedCold: false, Opus);
        _baselines.Record(other, Usage(write1h: Prefix), predictedCold: false, Opus);

        _baselines.DropFingerprint(Key.Fingerprint);

        Assert.Null(_baselines.Get(Key));
        Assert.Null(_baselines.Get(low));
        Assert.Equal(Prefix, _baselines.Get(other));
    }

    private static TokenUsage Usage(long write1h = 0, long read = 0) => new(12, write1h, 20, read, 100);

    private CacheReport Record(TokenUsage usage, bool predictedCold = false) => _baselines.Record(Key, usage, predictedCold, Opus);
}
