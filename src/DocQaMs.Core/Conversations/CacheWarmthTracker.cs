using DocQaMs.Core.Answering;

namespace DocQaMs.Core.Conversations;

/// <summary>What the app believes about one corpus cache entry.</summary>
public enum CacheWarmthStatus
{
    /// <summary>No request is known to have used the entry within its TTL: the next question writes it.</summary>
    Cold,

    /// <summary>A recent request wrote or read the entry, so it is probably still cached.</summary>
    ProbablyWarm,

    /// <summary>
    /// The entry is cold, and a request predicted to write it is waiting for its first event. Asking now may
    /// pay for a second write.
    /// </summary>
    ColdWriteInProgress,
}

/// <summary>
/// The predicted state of one corpus cache entry, for the cost preview. Only this app's own requests are
/// known, so "warm" is always "probably warm".
/// </summary>
/// <param name="Status">The prediction.</param>
/// <param name="WarmUntil">
/// Until when the entry is shown as probably warm, when <paramref name="Status"/> is
/// <see cref="CacheWarmthStatus.ProbablyWarm"/>; otherwise <see langword="null"/>.
/// </param>
public sealed record CacheWarmth(CacheWarmthStatus Status, DateTimeOffset? WarmUntil)
{
    /// <summary>Whether the entry is predicted to be cached.</summary>
    public bool IsProbablyWarm => Status == CacheWarmthStatus.ProbablyWarm;
}

/// <summary>
/// Predicts, per corpus cache entry, whether the next question will read the corpus or write it again.
/// </summary>
/// <remarks>
/// <para>
/// Each write or read of an entry refreshes its 1-hour TTL. The time is taken when the server reports the
/// usage, at <see cref="AnswerEvent.UsageStarted"/>, not when the answer completes, so a long answer does
/// not shorten the prediction, and a stopped or failed turn that used the cache still counts.
/// </para>
/// <para>
/// An entry is shown as probably warm until <see cref="ExpiryMargin"/> before its TTL runs out, which covers
/// a question sent just before the expiry. A cache warmed by some other client is unknown here; it simply
/// turns a predicted write into a cheaper read.
/// </para>
/// <para>Thread-safe: every conversation and every tab shares one instance.</para>
/// </remarks>
public sealed class CacheWarmthTracker
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<CorpusCacheKey, DateTimeOffset> _lastActivity = [];
    private readonly Dictionary<CorpusCacheKey, int> _coldWritesInProgress = [];

    /// <summary>Creates the tracker.</summary>
    /// <param name="time">The clock.</param>
    public CacheWarmthTracker(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>The corpus entry's TTL: the 1-hour breakpoint on the last document block.</summary>
    public static TimeSpan CorpusTtl { get; } = TimeSpan.FromHours(1);

    /// <summary>How long before the TTL runs out an entry stops being shown as probably warm.</summary>
    public static TimeSpan ExpiryMargin { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The prediction for one entry, now.</summary>
    /// <param name="key">The entry.</param>
    /// <returns>Cold, probably warm until a given time, or cold with a write in progress.</returns>
    public CacheWarmth GetWarmth(CorpusCacheKey key)
    {
        lock (_gate)
        {
            return WarmthOf(key, _time.GetUtcNow());
        }
    }

    /// <summary>
    /// Records that a request for <paramref name="key"/> is about to be sent. When the entry is predicted cold,
    /// it is marked as having a cold write in progress until the request's
    /// <see cref="CacheRequest.Started"/> or <see cref="CacheRequest.End"/>.
    /// </summary>
    internal CacheRequest BeginRequest(CorpusCacheKey key)
    {
        lock (_gate)
        {
            bool predictedCold = !WarmthOf(key, _time.GetUtcNow()).IsProbablyWarm;
            if (predictedCold)
            {
                _coldWritesInProgress[key] = _coldWritesInProgress.GetValueOrDefault(key) + 1;
            }

            return new CacheRequest(this, key, predictedCold);
        }
    }

    /// <summary>Records the server's report of a request's input side: any corpus write or read refreshes the TTL.</summary>
    internal void RecordUsage(CorpusCacheKey key, TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        // The 1-hour write is the corpus itself. A read always covers the corpus prefix, since any later cache
        // entry in the conversation extends it.
        if (usage.CacheWrite1h <= 0 && usage.CacheRead <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _lastActivity[key] = _time.GetUtcNow();
        }
    }

    private void EndColdWrite(CorpusCacheKey key)
    {
        lock (_gate)
        {
            int remaining = _coldWritesInProgress.GetValueOrDefault(key) - 1;
            if (remaining > 0)
            {
                _coldWritesInProgress[key] = remaining;
            }
            else
            {
                _coldWritesInProgress.Remove(key);
            }
        }
    }

    private CacheWarmth WarmthOf(CorpusCacheKey key, DateTimeOffset now)
    {
        if (_lastActivity.TryGetValue(key, out DateTimeOffset last))
        {
            DateTimeOffset warmUntil = last + CorpusTtl - ExpiryMargin;
            if (now < warmUntil)
            {
                return new CacheWarmth(CacheWarmthStatus.ProbablyWarm, warmUntil);
            }
        }

        return _coldWritesInProgress.ContainsKey(key)
            ? new CacheWarmth(CacheWarmthStatus.ColdWriteInProgress, null)
            : new CacheWarmth(CacheWarmthStatus.Cold, null);
    }

    /// <summary>
    /// One request's part in the tracker: whether it was predicted cold, and the end of its cold-write mark.
    /// </summary>
    internal sealed class CacheRequest
    {
        private readonly CacheWarmthTracker _tracker;
        private int _ended;

        internal CacheRequest(CacheWarmthTracker tracker, CorpusCacheKey key, bool predictedCold)
        {
            _tracker = tracker;
            Key = key;
            PredictedCold = predictedCold;
        }

        /// <summary>The entry the request reads or writes.</summary>
        public CorpusCacheKey Key { get; }

        /// <summary>Whether the tracker expected this request to write the corpus.</summary>
        public bool PredictedCold { get; }

        /// <summary>The request's usage arrived: record it, then end the cold-write mark.</summary>
        public void Started(TokenUsage usage)
        {
            // Recorded first, so the entry never reads as plainly cold in between.
            _tracker.RecordUsage(Key, usage);
            End();
        }

        /// <summary>Ends the cold-write mark, if this request set one. Safe to call more than once.</summary>
        public void End()
        {
            if (PredictedCold && Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _tracker.EndColdWrite(Key);
            }
        }
    }
}
