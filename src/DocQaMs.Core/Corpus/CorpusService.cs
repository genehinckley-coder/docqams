using System.Reflection;
using AngleSharp.Html.Parser;
using Anthropic.Exceptions;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// Holds the app's current corpus, loads it in the background, and publishes its
/// <see cref="CorpusState"/>.
/// </summary>
/// <remarks>
/// <para>
/// A load extracts the pages, assembles and size-guards the corpus, compares its fingerprint with the
/// previous load's, and records the new one, as the prototype's <c>load_corpus</c> does. A failure never
/// stops the app from starting: it is published as <see cref="CorpusState.Failed"/>, which the UI shows
/// with a retry. For example, no network at startup means the token count fails.
/// </para>
/// <para>
/// A reload replaces the current corpus only if extraction, the size guard and the state write all
/// succeed. Until then, and after a failed reload, the current corpus stays in use.
/// </para>
/// </remarks>
public sealed class CorpusService : ICorpusSource, IDisposable
{
    private readonly CorpusLoader _loader;
    private readonly DocQaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CorpusService> _logger;
    private readonly Lock _gate = new();

    // Cancelled when the app shuts down, never disposed: a load still in flight may be reading its token.
    private readonly CancellationTokenSource _shutdown = new();

    private CorpusState _state = new CorpusState.Loading();
    private Task? _load;

    /// <summary>Creates the service. Nothing is loaded until <see cref="ReloadAsync"/> is called.</summary>
    /// <param name="loader">Assembles and size-guards the corpus.</param>
    /// <param name="options">The corpus folder, state file, token ceiling and prices.</param>
    /// <param name="time">Stamps each load.</param>
    /// <param name="logger">Logs each outcome, with the full exception on failure.</param>
    public CorpusService(CorpusLoader loader, IOptions<DocQaOptions> options, TimeProvider time, ILogger<CorpusService> logger)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _loader = loader;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// This build's AngleSharp version, recorded in the state file so that a parser upgrade is named as the
    /// likely cause of changed text.
    /// </summary>
    public static string ParserVersion { get; } = ReadParserVersion();

    /// <summary>The current state. Each read returns an immutable snapshot.</summary>
    public CorpusState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Raised after <see cref="State"/> changes, on whichever thread changed it. A handler that throws is
    /// logged and does not affect the load or the other handlers.
    /// </summary>
    /// <remarks>
    /// The event carries no value on purpose: handlers read <see cref="State"/>, which may already be newer
    /// than the change that raised this notification. Every change raises one after it is made, so the last
    /// notification always reads the final state. Passing each state along instead could deliver a stale one
    /// after a newer one.
    /// </remarks>
    public event Action? StateChanged;

    /// <summary>
    /// Loads the corpus: the load at startup, a retry after a failure, and a reload all come here.
    /// </summary>
    /// <remarks>
    /// A call while a load is in progress joins that load instead of starting another.
    /// </remarks>
    /// <returns>A task that completes when the load's outcome is published in <see cref="State"/>. It never faults.</returns>
    /// <exception cref="ObjectDisposedException">The app is shutting down.</exception>
    public Task ReloadAsync()
    {
        Task load;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
            if (_load is { IsCompleted: false })
            {
                return _load;
            }

            _state = _state is CorpusState.Ready ready
                ? ready with { IsReloading = true, ReloadFailure = null }
                : new CorpusState.Loading();
            CancellationToken shutdown = _shutdown.Token;
            load = _load = Task.Run(() => LoadAsync(shutdown), CancellationToken.None);
        }

        RaiseStateChanged();
        return load;
    }

    /// <summary>Cancels a load in progress. The app is shutting down.</summary>
    public void Dispose() => _shutdown.Cancel();

    /// <summary>A short explanation of a failed load, for the user. The full exception goes to the log.</summary>
    internal static string DescribeFailure(Exception exception) => exception switch
    {
        // These messages are written for the user already, and name the file or the options.
        CorpusExtractionException or CorpusTooLargeException => exception.Message,

        // From ApiKeyLoader, whose messages name the key file and never contain the key.
        FileNotFoundException or InvalidDataException => exception.Message,

        AnthropicUnauthorizedException =>
            "Authentication failed: the API rejected the key in the file DocQaMs:ApiKeyPath names.",
        AnthropicRateLimitException =>
            "The API rate-limited the corpus token count. Wait a minute, then retry.",
        AnthropicApiException =>
            $"The API returned an error while counting the corpus tokens: {exception.Message}",
        AnthropicIOException or HttpRequestException =>
            "Could not reach the API to count the corpus tokens. Check the network connection, then retry.",
        OperationCanceledException =>
            "Counting the corpus tokens timed out. Check the network connection, then retry.",
        IOException or UnauthorizedAccessException =>
            $"A corpus page or the state file could not be read or written: {exception.Message}",
        _ => $"Loading the corpus failed unexpectedly ({exception.GetType().Name}); the log has the details.",
    };

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        CorpusState.Ready loaded;
        try
        {
            IReadOnlyList<ExtractedDocument> documents = CorpusExtractor.ExtractCorpus(_options.CorpusDirectory);
            Corpus corpus = await _loader.AssembleAsync(documents, _options.TokenCeiling, cancellationToken);

            // Recorded only after the corpus passes the guard, and it replaces the previous fingerprint, so a
            // change is reported by the first load that sees it rather than by every load after.
            FingerprintCheck check = CorpusStateFile.Compare(corpus, _options.StateFilePath, ParserVersion);
            DateTimeOffset now = _time.GetUtcNow();
            CorpusStateFile.Record(corpus, _options.StateFilePath, ParserVersion, now);

            loaded = new CorpusState.Ready(corpus, check, check.Describe(corpus, _options.Prices), now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is shutting down; nobody is left to show the outcome to.
            return;
        }
        catch (Exception ex)
        {
            string reason = DescribeFailure(ex);
            _logger.LogError(ex, "The corpus could not be loaded: {Reason}", reason);
            Publish(current => current is CorpusState.Ready ready
                ? ready with { IsReloading = false, ReloadFailure = reason }
                : new CorpusState.Failed(reason, _time.GetUtcNow()));
            return;
        }

        LogLoaded(loaded);
        Publish(_ => loaded);
    }

    private void LogLoaded(CorpusState.Ready loaded)
    {
        LogLevel level = loaded.Check.Status is FingerprintStatus.Changed or FingerprintStatus.Unreadable
            ? LogLevel.Warning
            : LogLevel.Information;
        _logger.Log(
            level,
            "Corpus loaded: {Documents} documents, {Tokens:N0} tokens at most of the {Ceiling:N0}-token ceiling, fingerprint {Fingerprint}. {Check}",
            loaded.Corpus.Documents.Count,
            loaded.Corpus.LargestTokenCount,
            _options.TokenCeiling,
            loaded.Corpus.Fingerprint,
            loaded.CheckDescription);
    }

    private void Publish(Func<CorpusState, CorpusState> transition)
    {
        lock (_gate)
        {
            _state = transition(_state);
        }

        RaiseStateChanged();
    }

    // Outside the lock, and one handler at a time, so a slow, disposed or throwing subscriber cannot hold
    // up the load or starve the others.
    private void RaiseStateChanged()
    {
        if (StateChanged is not { } handlers)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A corpus state subscriber threw; the other subscribers were still notified.");
            }
        }
    }

    private static string ReadParserVersion()
    {
        Assembly assembly = typeof(HtmlParser).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        // An informational version may carry "+<commit>" build metadata, which is not a different parser.
        return version?.Split('+')[0] ?? "unknown";
    }
}
