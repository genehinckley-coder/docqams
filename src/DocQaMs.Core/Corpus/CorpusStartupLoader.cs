using Microsoft.Extensions.Hosting;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// Starts loading the corpus when the app starts, without making startup wait for it.
/// </summary>
/// <remarks>
/// The load is not awaited: it counts about 200,000 tokens through the API, and its outcome is published
/// through <see cref="CorpusService.State"/>, which the UI renders as Loading until then.
/// </remarks>
internal sealed class CorpusStartupLoader(CorpusService corpus) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = corpus.ReloadAsync();
        return Task.CompletedTask;
    }

    // CorpusService cancels a load still in progress when the container disposes it.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
