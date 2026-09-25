using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocQaMs.Core.Tests.Live;

/// <summary>
/// Plan item 3, against the real API: the C# request counts exactly the pinned total on each model. Free
/// (<c>count_tokens</c> is not billed), but skipped unless <c>DOCQA_LIVE=1</c>, as every live test is.
/// </summary>
/// <remarks>
/// <para>
/// The totals were once the prototype's 199,749. Adding the Summary/Detail structure to the system prompt
/// made it 174 tokens longer, so the totals are now DocQA's own. The document blocks still match the
/// prototype's field for field, which <see cref="DocQaMs.Core.Tests.Corpus.CorpusBlocksGoldenTests"/> checks offline.
/// </para>
/// <para>
/// In the non-parallel collection, because another live test sets process-wide ANTHROPIC_* variables that
/// a client built here could read.
/// </para>
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CorpusTokenCountLiveTests
{
    // Measured with count_tokens on 2026-09-24. Opus 5.5's tokenizer counts 2 more than the others'.
    private static readonly Dictionary<string, long> ExpectedTokens = new(StringComparer.Ordinal)
    {
        [ModelIds.ClaudeOpus5] = 199_923,
        [ModelIds.ClaudeOpus55] = 199_925,
        [ModelIds.ClaudeSonnet5] = 199_923,
    };

    [Fact]
    public async Task TheAssembledCorpus_CountsExactlyThePinnedTotal_OnEveryModel()
    {
        LiveApi.RequireEnabled();
        var capture = new CaptureHandler();
        using AnthropicClientProvider provider = LiveApi.CreateProvider(capture);
        var loader = new CorpusLoader(new ApiTokenCounter(() => provider), NullLogger<CorpusLoader>.Instance);

        DocQaMs.Core.Corpus.Corpus corpus = await loader.AssembleAsync(
            CorpusExtractor.ExtractCorpus(RepoPaths.Combine("admincode")),
            LiveApi.Options.TokenCeiling,
            TestContext.Current.CancellationToken);

        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            LiveApi.Log($"corpus on {model.Id}: {corpus.TokenCounts[model.Id]:N0} tokens (pinned: {ExpectedTokens[model.Id]:N0})");
        }

        foreach (CorpusDocument document in corpus.Documents)
        {
            LiveApi.Log($"  {document.FileName}: about {document.ApproximateTokens:N0} tokens alone");
        }

        Assert.All(CacheAffecting.SelectableModels, model => Assert.Equal(ExpectedTokens[model.Id], corpus.TokenCounts[model.Id]));
        Assert.All(corpus.Documents, document => Assert.NotNull(document.ApproximateTokens));
        Assert.Equal(CacheAffecting.SelectableModels.Count + corpus.Documents.Count, capture.Requests.Count);
    }
}
