using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocQaMs.Claude.Tests;

/// <summary>
/// <see cref="ClaudeServiceCollectionExtensions.AddDocQaMsClaude"/>: the provider and the counter are
/// registered, and the key file is read only when a request is about to be made.
/// </summary>
public sealed class ClaudeServiceCollectionExtensionsTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("docqams-claude-di-");

    private string KeyPath => Path.Combine(_folder.FullName, "key.txt");

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public void TheProviderAndCounter_AreRegistered_AsOneSharedClient()
    {
        File.WriteAllText(KeyPath, $"Expires 2027-01-31\n{ClaudeTestClient.FakeKey}\n");
        using ServiceProvider services = Build();

        IChatClientProvider chatProvider = services.GetRequiredService<IChatClientProvider>();

        ClaudeChatClientProvider claude = Assert.IsType<ClaudeChatClientProvider>(chatProvider);
        Assert.Same(claude, services.GetRequiredService<ClaudeChatClientProvider>());
        Assert.Same(chatProvider, services.GetRequiredService<IChatClientProvider>());
        Assert.IsType<ClaudeTokenCounter>(services.GetRequiredService<ITokenCounter>());
        Assert.IsType<ClaudeChatClient>(chatProvider.ForEffort(EffortLevel.High));
        Assert.Equal(
            TestSettings.ValidOptions().RequestTimeouts[EffortLevel.High],
            chatProvider.ForEffort(EffortLevel.High).GetService<Anthropic.IAnthropicClient>()?.Timeout);
    }

    [Fact]
    public async Task TheKeyFile_IsReadOnlyWhenARequestIsAboutToBeMade()
    {
        // No key file yet: registering, building and resolving the counter must not read it.
        using ServiceProvider services = Build();
        ITokenCounter counter = services.GetRequiredService<ITokenCounter>();

        // A count without a key fails, as a failure the UI can show and retry.
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => counter.CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, TestContext.Current.CancellationToken));
        Assert.Throws<FileNotFoundException>(() => services.GetRequiredService<IChatClientProvider>());

        // Once the key exists, the next use reads it.
        File.WriteAllText(KeyPath, ClaudeTestClient.FakeKey);
        Assert.IsType<ClaudeChatClientProvider>(services.GetRequiredService<IChatClientProvider>());
    }

    [Fact]
    public void AMalformedKeyFile_SurfacesWhenTheProviderIsResolved()
    {
        File.WriteAllText(KeyPath, "not a key\n");
        using ServiceProvider services = Build();

        Assert.Throws<InvalidDataException>(() => services.GetRequiredService<IChatClientProvider>());
    }

    [Fact]
    public void ExistingRegistrations_AreKept()
    {
        var substitute = new SubstituteProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(substitute);
        AddPrerequisites(services);

        services.AddDocQaMsClaude();
        using ServiceProvider built = services.BuildServiceProvider();

        Assert.Same(substitute, built.GetRequiredService<IChatClientProvider>());
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        AddPrerequisites(services);
        services.AddDocQaMsClaude();
        return services.BuildServiceProvider();
    }

    /// <summary>What <c>AddDocQaCore</c> and the host would otherwise register.</summary>
    private void AddPrerequisites(IServiceCollection services)
    {
        DocQaOptions options = TestSettings.ValidOptions();
        options.ApiKeyPath = KeyPath;
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
    }

    private sealed class SubstituteProvider : IChatClientProvider
    {
        public IChatClient ForEffort(EffortLevel level) => throw new NotSupportedException();
    }
}
