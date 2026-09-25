using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Tests.Answering;

public sealed class TurnCostTests
{
    private static readonly ModelPrices Opus5 = new()
    {
        InputPerMTok = 5m, CacheWrite5mPerMTok = 6.25m, CacheWrite1hPerMTok = 10m, CacheReadPerMTok = 0.5m, OutputPerMTok = 25m,
    };

    private static readonly ModelPrices Sonnet5 = new()
    {
        InputPerMTok = 2m, CacheWrite5mPerMTok = 2.5m, CacheWrite1hPerMTok = 4m, CacheReadPerMTok = 0.2m, OutputPerMTok = 10m,
    };

    private static readonly TokenUsage MillionEach = new(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000);

    [Fact]
    public void EachBucket_IsPricedAtItsOwnRate()
    {
        TurnCost cost = TurnCost.For(MillionEach, Opus5);

        Assert.Equal(new TurnCost(5m, 10m, 6.25m, 0.5m, 25m), cost);
        Assert.Equal(46.75m, cost.Total);
    }

    [Fact]
    public void TheSameUsage_CostsDifferentlyOnEachModel()
    {
        Assert.Equal(new TurnCost(2m, 4m, 2.5m, 0.2m, 10m), TurnCost.For(MillionEach, Sonnet5));
        Assert.NotEqual(TurnCost.For(MillionEach, Opus5).Total, TurnCost.For(MillionEach, Sonnet5).Total);
    }

    [Fact]
    public void AColdCorpusWrite_CostsWhatTheCostReferenceSays()
    {
        // 199,745 tokens in the 1-hour prefix, measured by the prototype.
        TurnCost cost = TurnCost.For(new TokenUsage(0, 199_745, 0, 0, 0), Opus5);

        Assert.Equal(1.99745m, cost.Total);
    }

    [Fact]
    public void Costs_AddBucketByBucket()
    {
        TurnCost sum = new TurnCost(1m, 2m, 3m, 4m, 5m).Add(new TurnCost(0.1m, 0.2m, 0.3m, 0.4m, 0.5m));

        Assert.Equal(new TurnCost(1.1m, 2.2m, 3.3m, 4.4m, 5.5m), sum);
        Assert.Equal(sum, sum.Add(TurnCost.Zero));
    }

    [Fact]
    public void Usage_SumsTheCacheBucketsAndTheContext()
    {
        var usage = new TokenUsage(Input: 12, CacheWrite1h: 200, CacheWrite5m: 30, CacheRead: 4_000, Output: 900);

        Assert.Equal(230, usage.CacheWrite);
        Assert.Equal(4_242, usage.ContextTokens);
        Assert.True(usage.HasCacheActivity);
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 1, 0, true)]
    [InlineData(0, 0, 1, true)]
    public void CacheActivity_IsAnyWriteOrRead(long write1h, long write5m, long read, bool active) =>
        Assert.Equal(active, new TokenUsage(10, write1h, write5m, read, 100).HasCacheActivity);
}
