using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private static readonly string ContentRoot = Path.Combine(Path.GetTempPath(), "docqa-content-root");

    [Fact]
    public void RelativePaths_ResolveAgainstTheContentRoot()
    {
        using ServiceProvider services = Build(TestSettings.Valid());

        DocQaOptions options = services.GetRequiredService<IOptions<DocQaOptions>>().Value;

        Assert.Equal(Path.Combine(ContentRoot, "corpus"), options.CorpusDirectory);
        Assert.Equal(Path.Combine(ContentRoot, "key.txt"), options.ApiKeyPath);
        Assert.Equal(Path.Combine(ContentRoot, "state.json"), options.StateFilePath);
    }

    [Fact]
    public void StartupValidation_IsWiredIn_AndReportsACrossCheckFailure()
    {
        Dictionary<string, string?> settings = TestSettings.Valid();
        settings.Remove($"DocQaMs:MaxTokens:{EffortLevel.XHigh}");
        using ServiceProvider services = Build(settings);

        // IStartupValidator is what the host runs at startup for ValidateOnStart.
        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("MaxTokens has no value for the effort level XHigh", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupValidation_ReportsADataAnnotationFailure()
    {
        Dictionary<string, string?> settings = TestSettings.Valid();
        settings["DocQaMs:ApiKeyPath"] = "";
        using ServiceProvider services = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(nameof(DocQaOptions.ApiKeyPath), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClock_IsTheSystemClock()
    {
        using ServiceProvider services = Build(TestSettings.Valid());

        Assert.Same(TimeProvider.System, services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void TheCorpusService_Resolves_WithoutReadingTheKeyFile()
    {
        // TestSettings' key file does not exist, so reading it here would throw.
        using ServiceProvider services = Build(TestSettings.Valid());

        Assert.IsType<CorpusState.Loading>(services.GetRequiredService<CorpusService>().State);
        Assert.IsType<ApiTokenCounter>(services.GetRequiredService<ITokenCounter>());
    }

    [Fact]
    public async Task TheAnswerService_Resolves_WithoutReadingTheKeyFile_AndReportsItMissingWhenItAnswers()
    {
        using ServiceProvider services = Build(TestSettings.Valid());

        IAnswerService answers = Assert.IsType<AnswerService>(services.GetRequiredService<IAnswerService>());

        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (AnswerEvent _ in answers.StreamAsync(
                AnswerSettings.Default, AnswerHarness.FirstQuestion, TestContext.Current.CancellationToken))
            {
            }
        });

        AnswerFailedException failure = Assert.IsType<AnswerFailedException>(ex);
        Assert.Equal(AnswerFailureKind.Credentials, failure.Kind);
        Assert.Contains(Path.Combine(ContentRoot, "key.txt"), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuoteChecker_ResolvesAsASingleton()
    {
        using ServiceProvider services = Build(TestSettings.Valid());

        Assert.Same(services.GetRequiredService<QuoteChecker>(), services.GetRequiredService<QuoteChecker>());
    }

    [Fact]
    public void TheConversationStore_IsOneSingleton_OverTheCorpusService_WithoutAHost()
    {
        using ServiceProvider services = Build(TestSettings.Valid());

        // No IHostApplicationLifetime is registered here, so this also shows the store resolves without one.
        ConversationStore store = services.GetRequiredService<ConversationStore>();

        Assert.Same(store, services.GetRequiredService<IConversationStore>());
        Assert.Same(services.GetRequiredService<CorpusService>(), services.GetRequiredService<ICorpusSource>());
        Assert.Same(services.GetRequiredService<CacheWarmthTracker>(), services.GetRequiredService<CacheWarmthTracker>());
        Assert.Same(services.GetRequiredService<CacheBaselines>(), services.GetRequiredService<CacheBaselines>());
        Assert.Same(services.GetRequiredService<CostEstimator>(), services.GetRequiredService<CostEstimator>());
        Assert.Empty(store.List());
    }

    [Fact]
    public void TheCorpusLoad_StartsWithTheHost()
    {
        using ServiceProvider services = Build(TestSettings.Valid());

        Assert.Single(services.GetServices<IHostedService>(), service => service is CorpusStartupLoader);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings) =>
        new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddDocQaCore(TestSettings.ToConfiguration(settings), ContentRoot)
            .BuildServiceProvider();
}
