using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Conversations;

/// <summary>How a completed turn used the corpus cache.</summary>
public enum CacheStatus
{
    /// <summary>The response reports no cache write and no cache read at all.</summary>
    NotCached,

    /// <summary>The turn wrote the corpus to the cache, as expected for this entry.</summary>
    ColdWrite,

    /// <summary>The turn read the corpus from the cache.</summary>
    Read,

    /// <summary>The turn read less of the corpus than it should have, and the tracker did not predict it.</summary>
    Miss,
}

/// <summary>Why a turn missed the corpus cache.</summary>
public enum CacheMissKind
{
    /// <summary>The turn wrote the whole corpus again: the entry had expired or been evicted. The next turns should read it.</summary>
    Expired,

    /// <summary>The turn neither read nor wrote the whole prefix: it no longer matches, so every turn pays full price.</summary>
    PrefixChanged,
}

/// <summary>How one completed turn used the corpus cache.</summary>
/// <param name="Status">The badge: cold write, read, miss or not cached.</param>
/// <param name="MissKind">For a <see cref="CacheStatus.Miss"/>, why; otherwise <see langword="null"/>.</param>
/// <param name="Warning">For a <see cref="CacheStatus.Miss"/>, the warning as the UI shows it; otherwise <see langword="null"/>.</param>
public sealed record CacheReport(CacheStatus Status, CacheMissKind? MissKind = null, string? Warning = null);

/// <summary>
/// The corpus prefix each cache entry is expected to read, and the check of every completed turn against it.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the prototype's <c>Session.record</c>. Prefix matching fails silently: a correct answer at
/// several times the price, with no error. So each turn's cache read is compared with the prefix the first
/// turn billed, and a shortfall is reported loudly.
/// </para>
/// <para>
/// A baseline is held per corpus cache entry at app level and shared by every conversation that uses the
/// entry, as the prototype's survived <c>/reset</c>. It is set by the first turn that writes or reads
/// anything, to the 1-hour write plus the read. That turn may write the prefix, read an entry that is still
/// warm, or split between the two, so the sum is an upper bound, which
/// <see cref="DocQaOptions.CacheReadToleranceFraction"/> absorbs.
/// </para>
/// </remarks>
public sealed class CacheBaselines
{
    private readonly double _toleranceFraction;
    private readonly Lock _gate = new();
    private readonly Dictionary<CorpusCacheKey, long> _baselines = [];

    /// <summary>Creates the baselines, with the configured tolerance.</summary>
    /// <param name="options">The options holding <see cref="DocQaOptions.CacheReadToleranceFraction"/>.</param>
    public CacheBaselines(IOptions<DocQaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _toleranceFraction = options.Value.CacheReadToleranceFraction;
    }

    /// <summary>The prefix tokens an entry is expected to read, or <see langword="null"/> before any turn used it.</summary>
    /// <param name="key">The entry.</param>
    /// <returns>The baseline, if set.</returns>
    public long? Get(CorpusCacheKey key)
    {
        lock (_gate)
        {
            return _baselines.TryGetValue(key, out long baseline) ? baseline : null;
        }
    }

    /// <summary>
    /// Forgets the baselines of every entry for <paramref name="fingerprint"/>. Called when a reload changes the
    /// corpus: the old prefix is no longer what new conversations send.
    /// </summary>
    /// <param name="fingerprint">The replaced corpus's fingerprint.</param>
    public void DropFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        lock (_gate)
        {
            foreach (CorpusCacheKey key in _baselines.Keys.Where(key => key.Fingerprint == fingerprint).ToList())
            {
                _baselines.Remove(key);
            }
        }
    }

    /// <summary>
    /// Checks one completed turn against its entry's baseline, setting the baseline when there is none yet.
    /// </summary>
    /// <param name="key">The entry the turn's request used.</param>
    /// <param name="usage">The turn's usage.</param>
    /// <param name="predictedCold">
    /// Whether the warmth tracker expected this turn to write the corpus. A full write it predicted is a cold
    /// write, not a miss.
    /// </param>
    /// <param name="prices">The model's prices, for the extra cost a miss names.</param>
    internal CacheReport Record(CorpusCacheKey key, TokenUsage usage, bool predictedCold, ModelPrices prices)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(prices);

        // The corpus breakpoint is the only 1-hour one; the conversation tail is written at 5 minutes and is
        // not part of the prefix.
        long prefixWrite = usage.CacheWrite1h;
        long read = usage.CacheRead;

        lock (_gate)
        {
            if (_baselines.TryGetValue(key, out long expected))
            {
                long least = expected - Tolerance(expected);
                if (read >= least)
                {
                    return new CacheReport(CacheStatus.Read);
                }

                bool wroteWholePrefix = prefixWrite >= least;
                if (predictedCold && wroteWholePrefix)
                {
                    return new CacheReport(CacheStatus.ColdWrite);
                }

                return wroteWholePrefix
                    ? new CacheReport(CacheStatus.Miss, CacheMissKind.Expired, ExpiredWarning(expected, read, prefixWrite, prices))
                    : new CacheReport(CacheStatus.Miss, CacheMissKind.PrefixChanged, PrefixChangedWarning(expected, read));
            }

            if (prefixWrite + read == 0)
            {
                return new CacheReport(CacheStatus.NotCached);
            }

            _baselines[key] = prefixWrite + read;
            return new CacheReport(prefixWrite >= read ? CacheStatus.ColdWrite : CacheStatus.Read);
        }
    }

    /// <summary>How far short of <paramref name="expected"/> a read may fall without being a miss. Truncated, as Python's <c>int()</c>.</summary>
    private long Tolerance(long expected) => (long)(expected * _toleranceFraction);

    private static string Shortfall(long expected, long read) => FormattableString.Invariant(
        $"CACHE MISS: this turn read {read:N0} cached tokens, short of the {expected:N0} the corpus prefix should give.");

    private static string ExpiredWarning(long expected, long read, long prefixWrite, ModelPrices prices)
    {
        decimal extra = expected * (prices.CacheWrite1hPerMTok - prices.CacheReadPerMTok) / 1_000_000m;
        return Shortfall(expected, read)
            + FormattableString.Invariant($" It wrote the corpus afresh instead ({prefixWrite:N0} tokens), so the 1-hour entry had expired or been evicted.")
            + FormattableString.Invariant($" That cost about ${extra:0.00} more than a read; the turns after it should read it again.");
    }

    private static string PrefixChangedWarning(long expected, long read) =>
        Shortfall(expected, read) +
        " The prefix no longer matches, so every turn is paying full input price. Check whether the corpus or a " +
        "cache-affecting value changed; reloading the corpus reports what differs.";
}
