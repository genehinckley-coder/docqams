using System.Text.Json.Nodes;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Plan item 3: parity with the prototype's assembled request, not just its extracted text. The carried-over
/// cost baseline also depends on the rendered titles and contexts and on the block order. All offline.
/// </summary>
public sealed class CorpusBlocksGoldenTests
{
    // Assembled once and shared: the corpus is read-only, and extraction takes about a second.
    private static readonly Lazy<Task<DocQaMs.Core.Corpus.Corpus>> RealCorpus =
        new(() => Corpora.OfAsync([.. CorpusExtractor.ExtractCorpus(RepoPaths.Combine("admincode"))]));

    [Fact]
    public async Task TheAssembledBlocks_MatchThePrototypes_FieldForField()
    {
        DocQaMs.Core.Corpus.Corpus corpus = await RealCorpus.Value;

        // The canonical serialization is what the SDK sends (ApiTokenCounterTests), so this compares the
        // request's blocks, in order and with the breakpoint on the last one only, with the prototype's.
        JsonArray assembled = JsonNode.Parse(CorpusFingerprint.CanonicalJson(corpus.RequestDocuments))!.AsArray();
        for (int i = 0; i < assembled.Count; i++)
        {
            JsonObject source = assembled[i]!["source"]!.AsObject();
            string text = (string)source["data"]!;
            source.Remove("data");
            source["chars"] = corpus.Documents[i].CharacterCount;
            source["data_sha256"] = CorpusStateFile.Sha256(text);
        }

        Assert.True(
            JsonNode.DeepEquals(BlocksGolden.Blocks, assembled),
            $"The assembled blocks differ from the prototype's.\nPrototype: {BlocksGolden.Blocks.ToJsonString()}\nThis port: {assembled.ToJsonString()}");
    }

    [Fact]
    public async Task ParagraphRanges_TileTheText_OfEveryRealDocument()
    {
        DocQaMs.Core.Corpus.Corpus corpus = await RealCorpus.Value;

        Assert.All(corpus.Documents, document =>
        {
            List<string> paragraphs = document.Paragraphs.Select(range => document.Text[range.Start..range.End]).ToList();

            Assert.Equal(document.Text, string.Join("\n\n", paragraphs));
            Assert.All(paragraphs, paragraph =>
            {
                Assert.NotEmpty(paragraph);
                Assert.DoesNotContain("\n\n", paragraph, StringComparison.Ordinal);
            });
        });
    }

    [Fact]
    public async Task TheRealCorpus_AssemblesToAStableFingerprint()
    {
        // Two independent extractions: determinism is load-bearing for the cache, so this compares whole
        // runs, not one run's blocks twice.
        DocQaMs.Core.Corpus.Corpus first = await RealCorpus.Value;
        DocQaMs.Core.Corpus.Corpus second = await Corpora.OfAsync([.. CorpusExtractor.ExtractCorpus(RepoPaths.Combine("admincode"))]);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }
}
