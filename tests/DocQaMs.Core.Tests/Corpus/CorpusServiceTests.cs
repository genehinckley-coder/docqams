using System.Net;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Plan item 3: the corpus service's states, and a reload that replaces the corpus only when everything
/// succeeds. Offline: the real corpus is extracted, and the token counter is a fake or a stubbed SDK.
/// </summary>
public sealed class CorpusServiceTests : IDisposable
{
    private readonly TempDirectory _folder = new();
    private readonly ListLogger<CorpusService> _log = new();

    private string StatePath => _folder.Combine("state.json");

    public void Dispose() => _folder.Dispose();

    // --- Loading ------------------------------------------------------------------------------------

    [Fact]
    public async Task ALoad_PublishesTheRealCorpus_AndRecordsItsFingerprint()
    {
        using CorpusService service = Create(new FakeTokenCounter());
        int changes = 0;
        service.StateChanged += () => Interlocked.Increment(ref changes);
        Assert.IsType<CorpusState.Loading>(service.State);

        await service.ReloadAsync();

        var ready = Assert.IsType<CorpusState.Ready>(service.State);
        Assert.Equal(CacheAffecting.DocumentLabels.Keys.Order(StringComparer.Ordinal), ready.Corpus.Documents.Select(document => document.FileName));
        Assert.Equal(FingerprintStatus.FirstRun, ready.Check.Status);
        Assert.Equal("No previous fingerprint on record; this one is now recorded.", ready.CheckDescription);
        Assert.False(ready.IsReloading);
        Assert.Null(ready.ReloadFailure);
        Assert.Equal(FingerprintStatus.Unchanged, CorpusStateFile.Compare(ready.Corpus, StatePath, CorpusService.ParserVersion).Status);

        // Loading when the load starts, and Ready when it ends.
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ASecondLoad_ReportsTheCorpusUnchanged_AndReplacesTheSnapshot()
    {
        using CorpusService service = Create(new FakeTokenCounter());
        await service.ReloadAsync();
        var first = Assert.IsType<CorpusState.Ready>(service.State);

        await service.ReloadAsync();

        var second = Assert.IsType<CorpusState.Ready>(service.State);
        Assert.Equal(FingerprintStatus.Unchanged, second.Check.Status);
        Assert.NotSame(first.Corpus, second.Corpus);
        Assert.Equal(first.Corpus.Fingerprint, second.Corpus.Fingerprint);
    }

    [Fact]
    public async Task TheStartupLoader_StartsALoad_ThatALaterReloadJoins()
    {
        using var release = new ManualResetEventSlim();
        var counter = new FakeTokenCounter(call =>
        {
            release.Wait(TestContext.Current.CancellationToken);
            return CountOf(call);
        });
        using CorpusService service = Create(counter);

        await new CorpusStartupLoader(service).StartAsync(TestContext.Current.CancellationToken);
        Task joined = service.ReloadAsync();
        release.Set();
        await joined;

        Assert.IsType<CorpusState.Ready>(service.State);
        Assert.Equal(CacheAffecting.SelectableModels.Count + CacheAffecting.DocumentLabels.Count, counter.Calls.Count);
    }

    [Fact]
    public async Task ReloadsWhileOneIsInProgress_JoinTheSameLoad()
    {
        using var release = new ManualResetEventSlim();
        var counter = new FakeTokenCounter(call =>
        {
            release.Wait(TestContext.Current.CancellationToken);
            return CountOf(call);
        });
        using CorpusService service = Create(counter);

        Task first = service.ReloadAsync();
        Task second = service.ReloadAsync();
        release.Set();
        await first;

        Assert.Same(first, second);
        Assert.Equal(CacheAffecting.SelectableModels.Count + CacheAffecting.DocumentLabels.Count, counter.Calls.Count);
    }

    // --- Failures -----------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingCorpusFolder_FailsTheLoad_AndRecordsNothing()
    {
        string missing = _folder.Combine("no-such-folder");
        using CorpusService service = Create(new FakeTokenCounter(), options => options.CorpusDirectory = missing);

        await service.ReloadAsync();

        var failed = Assert.IsType<CorpusState.Failed>(service.State);
        Assert.Equal($"Corpus folder not found: {missing}", failed.Reason);
        Assert.False(File.Exists(StatePath));
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is CorpusExtractionException);
    }

    [Fact]
    public async Task AnOversizedCorpus_FailsTheLoad_AndRecordsNoFingerprint()
    {
        using CorpusService service = Create(new FakeTokenCounter(TestSettings.ValidOptions().TokenCeiling + 1));

        await service.ReloadAsync();

        var failed = Assert.IsType<CorpusState.Failed>(service.State);
        Assert.Contains("Nothing was truncated", failed.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task AStateFileThatCannotBeWritten_FailsTheLoad()
    {
        Directory.CreateDirectory(StatePath);
        using CorpusService service = Create(new FakeTokenCounter());

        await service.ReloadAsync();

        var failed = Assert.IsType<CorpusState.Failed>(service.State);
        Assert.StartsWith("A corpus page or the state file could not be read or written:", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedReload_KeepsTheCurrentCorpus_AndSaysWhy()
    {
        long corpusTokens = 1_000;
        using CorpusService service = Create(new FakeTokenCounter(call => call.Documents.Count > 1 ? corpusTokens : 1));
        await service.ReloadAsync();
        var loaded = Assert.IsType<CorpusState.Ready>(service.State);

        corpusTokens = TestSettings.ValidOptions().TokenCeiling + 1;
        await service.ReloadAsync();

        var kept = Assert.IsType<CorpusState.Ready>(service.State);
        Assert.Same(loaded.Corpus, kept.Corpus);
        Assert.False(kept.IsReloading);
        Assert.Contains("Nothing was truncated", kept.ReloadFailure, StringComparison.Ordinal);

        // A later successful reload clears the failure.
        corpusTokens = 1_000;
        await service.ReloadAsync();
        Assert.Null(Assert.IsType<CorpusState.Ready>(service.State).ReloadFailure);
    }

    [Fact]
    public async Task DuringAReload_TheCurrentCorpusStaysReady()
    {
        using var release = new ManualResetEventSlim(initialState: true);
        var counter = new FakeTokenCounter(call =>
        {
            release.Wait(TestContext.Current.CancellationToken);
            return CountOf(call);
        });
        using CorpusService service = Create(counter);
        await service.ReloadAsync();
        var loaded = Assert.IsType<CorpusState.Ready>(service.State);

        release.Reset();
        Task reload = service.ReloadAsync();

        var reloading = Assert.IsType<CorpusState.Ready>(service.State);
        Assert.True(reloading.IsReloading);
        Assert.Same(loaded.Corpus, reloading.Corpus);

        release.Set();
        await reload;
        Assert.False(Assert.IsType<CorpusState.Ready>(service.State).IsReloading);
    }

    [Fact]
    public async Task ASubscriberThatThrows_DoesNotStopTheLoad_OrTheOtherSubscribers()
    {
        using CorpusService service = Create(new FakeTokenCounter());
        int calls = 0;
        service.StateChanged += () => throw new ObjectDisposedException("a closed tab's component");
        service.StateChanged += () => Interlocked.Increment(ref calls);

        await service.ReloadAsync();

        Assert.IsType<CorpusState.Ready>(service.State);
        Assert.Equal(2, calls);
        Assert.Equal(2, _log.Entries.Count(entry => entry.Level == LogLevel.Error && entry.Exception is ObjectDisposedException));
    }

    [Fact]
    public async Task AMissingKeyFile_IsReportedByName()
    {
        string keyPath = _folder.Combine("missing-key.txt");
        var counter = new ApiTokenCounter(
            () => AnthropicClientProvider.Create(ApiKeyLoader.Load(keyPath), TestSettings.ValidOptions().RequestTimeouts, []));
        using CorpusService service = Create(counter);

        await service.ReloadAsync();

        var failed = Assert.IsType<CorpusState.Failed>(service.State);
        Assert.StartsWith($"API key file not found: {keyPath}.", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedKey_IsReportedAsAnAuthenticationFailure()
    {
        var stub = new StubHandler(_ => StubHandler.Json(
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""",
            HttpStatusCode.Unauthorized));
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(
            "sk-ant-test-key", TestSettings.ValidOptions().RequestTimeouts, [stub]);
        using CorpusService service = Create(new ApiTokenCounter(() => provider));

        await service.ReloadAsync();

        var failed = Assert.IsType<CorpusState.Failed>(service.State);
        Assert.StartsWith("Authentication failed", failed.Reason, StringComparison.Ordinal);
        Assert.Single(stub.Requests);
    }

    public static TheoryData<string, Exception, string> Failures => new()
    {
        { "connection", new HttpRequestException("No such host is known."), "Could not reach the API" },
        { "timeout", new TaskCanceledException(), "Counting the corpus tokens timed out" },
        { "access", new UnauthorizedAccessException("Access denied."), "A corpus page or the state file could not be read or written: Access denied." },
        { "key file", new InvalidDataException("API key file is empty: key.txt"), "API key file is empty: key.txt" },
        { "unexpected", new InvalidOperationException("Something odd."), "Loading the corpus failed unexpectedly (InvalidOperationException)" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void EachFailure_IsDescribedShortly(string kind, Exception exception, string expected) =>
        Assert.True(
            CorpusService.DescribeFailure(exception).StartsWith(expected, StringComparison.Ordinal),
            $"The {kind} failure was described as \"{CorpusService.DescribeFailure(exception)}\".");

    [Fact]
    public async Task AfterShutdown_ALoadIsRefused()
    {
        CorpusService service = Create(new FakeTokenCounter());
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ReloadAsync());
    }

    [Fact]
    public void TheParserVersion_IsAnAngleSharpVersion() =>
        Assert.Matches(@"^\d+\.\d+\.\d+$", CorpusService.ParserVersion);

    // The whole corpus counts 1,000; each document on its own counts 1.
    private static long CountOf(CountCall call) => call.Documents.Count > 1 ? 1_000 : 1;

    private CorpusService Create(ITokenCounter counter, Action<DocQaOptions>? configure = null)
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.CorpusDirectory = RepoPaths.Combine("admincode");
        options.StateFilePath = StatePath;
        configure?.Invoke(options);
        return new CorpusService(
            new CorpusLoader(counter, NullLogger<CorpusLoader>.Instance), Options.Create(options), TimeProvider.System, _log);
    }
}
