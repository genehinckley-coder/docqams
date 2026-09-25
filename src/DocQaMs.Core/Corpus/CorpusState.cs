namespace DocQaMs.Core.Corpus;

/// <summary>
/// The corpus as the app currently sees it: <see cref="Loading"/>, <see cref="Ready"/> or
/// <see cref="Failed"/>. The UI renders this state, with Ask available only when it is <see cref="Ready"/>.
/// </summary>
public abstract record CorpusState
{
    private CorpusState()
    {
    }

    /// <summary>The first load, or a retry after a failure, is in progress. Nothing can be asked yet.</summary>
    public sealed record Loading() : CorpusState;

    /// <summary>A corpus is loaded and current.</summary>
    /// <param name="Corpus">The current corpus.</param>
    /// <param name="Check">How it compared with the fingerprint the previous load recorded.</param>
    /// <param name="CheckDescription">The comparison as text for the user, with the cost of any change.</param>
    /// <param name="LoadedAt">When it was loaded and its fingerprint recorded.</param>
    /// <param name="IsReloading">
    /// Whether a reload is in progress. <paramref name="Corpus"/> stays current, and usable, until a reload
    /// succeeds.
    /// </param>
    /// <param name="ReloadFailure">
    /// Why the most recent reload failed, leaving <paramref name="Corpus"/> current; <see langword="null"/>
    /// when it did not fail.
    /// </param>
    public sealed record Ready(
        Corpus Corpus,
        FingerprintCheck Check,
        string CheckDescription,
        DateTimeOffset LoadedAt,
        bool IsReloading = false,
        string? ReloadFailure = null) : CorpusState;

    /// <summary>No corpus could be loaded. A retry is a new load.</summary>
    /// <param name="Reason">A short explanation for the user. The full exception is in the log.</param>
    /// <param name="FailedAt">When the load failed.</param>
    public sealed record Failed(string Reason, DateTimeOffset FailedAt) : CorpusState;
}
