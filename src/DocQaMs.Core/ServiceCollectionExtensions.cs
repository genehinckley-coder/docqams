using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core;

/// <summary>
/// Registers DocQaMs's core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DocQaOptions"/> with startup validation, the system clock, the answer service,
    /// the quote checker, the conversation store, and the corpus service, whose load starts in the
    /// background when the host starts.
    /// </summary>
    /// <remarks>
    /// Core talks to the model only through Microsoft.Extensions.AI, so a chat provider must also be
    /// registered: it supplies the <see cref="IChatClientProvider"/> the answer service uses and the
    /// <see cref="ITokenCounter"/> the corpus size guard uses. For Claude, that is <c>AddDocQaMsClaude()</c>
    /// in DocQaMs.Claude.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration containing the <c>DocQaMs</c> section.</param>
    /// <param name="contentRootPath">The folder relative paths in the options resolve against.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddDocQaCore(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        services.AddOptions<DocQaOptions>()
            .Bind(configuration.GetSection(DocQaOptions.SectionName))
            .PostConfigure(options =>
            {
                options.CorpusDirectory = ResolvePath(options.CorpusDirectory, contentRootPath);
                options.ApiKeyPath = ResolvePath(options.ApiKeyPath, contentRootPath);
                options.StateFilePath = ResolvePath(options.StateFilePath, contentRootPath);
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DocQaOptions>, DocQaOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);

        // The answer service resolves the chat client provider only when it answers, so the key file is read
        // only when a request is about to be made, and a missing key surfaces as a failure the UI can show
        // instead of stopping the app from starting. The provider package registers IChatClientProvider.
        services.TryAddSingleton<IAnswerService>(provider => new AnswerService(
            () => provider.GetRequiredService<IChatClientProvider>(),
            provider.GetRequiredService<IOptions<DocQaOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<AnswerService>>()));
        services.TryAddSingleton<QuoteChecker>();
        services.TryAddSingleton<CorpusLoader>();
        services.TryAddSingleton<CorpusService>();
        services.TryAddSingleton<ICorpusSource>(provider => provider.GetRequiredService<CorpusService>());
        services.AddHostedService<CorpusStartupLoader>();

        // Conversations live for the app's lifetime and are shared by every tab, as are the cache predictions
        // and baselines, which span conversations.
        services.TryAddSingleton<CacheWarmthTracker>();
        services.TryAddSingleton<CacheBaselines>();
        services.TryAddSingleton<CostEstimator>();
        services.TryAddSingleton<ConversationStore>();
        services.TryAddSingleton<IConversationStore>(provider => provider.GetRequiredService<ConversationStore>());

        return services;
    }

    private static string ResolvePath(string path, string contentRootPath) =>
        string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path, contentRootPath);
}
