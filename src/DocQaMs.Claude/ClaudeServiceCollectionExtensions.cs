using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocQaMs.Claude;

/// <summary>
/// Registers the Claude provider.
/// </summary>
public static class ClaudeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ClaudeChatClientProvider"/> as the app's <see cref="IChatClientProvider"/>, and
    /// <see cref="ClaudeTokenCounter"/> as its <see cref="ITokenCounter"/>. Call after
    /// <c>AddDocQaCore</c>, which registers the <see cref="DocQaOptions"/> they read.
    /// </summary>
    /// <remarks>
    /// The provider is built on first use, so the key file is read only when a request is about to be
    /// made, and a missing key surfaces as a failure the UI can show instead of stopping the app from
    /// starting. The counter resolves the provider when it counts, not when it is constructed, for the
    /// same reason. Existing registrations are kept, so a test can substitute either service.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddDocQaMsClaude(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider =>
        {
            DocQaOptions options = provider.GetRequiredService<IOptions<DocQaOptions>>().Value;
            ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return ClaudeChatClientProvider.Create(
                ApiKeyLoader.Load(options.ApiKeyPath),
                options.RequestTimeouts,
                [new RetryLoggingHandler(loggerFactory.CreateLogger<RetryLoggingHandler>())],
                loggerFactory);
        });
        services.TryAddSingleton<IChatClientProvider>(provider => provider.GetRequiredService<ClaudeChatClientProvider>());
        services.TryAddSingleton<ITokenCounter>(provider =>
            new ClaudeTokenCounter(() => provider.GetRequiredService<ClaudeChatClientProvider>()));

        return services;
    }
}
