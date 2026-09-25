using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;
using DocQaMs.Core.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Plan item 3: assembly, the size guard and the fingerprint, ported from the prototype's
/// <c>test_corpus.py</c>. All offline: the token counter is injected, so the guard is exercised at its
/// exact boundary without an API call.
/// </summary>
public sealed class CorpusLoaderTests
{
    private static readonly ExtractedDocument Alpha = new("a.htm", "Part A", "Alpha text.\n\na) First.");
    private static readonly ExtractedDocument Bravo = new("b.htm", "Part B", "Bravo text.");
    private static readonly ExtractedDocument Charlie = new("c.htm", "Part C", "Charlie.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Assembly -----------------------------------------------------------------------------------

    [Fact]
    public async Task Documents_AreAssembledInOrdinalFileNameOrder()
    {
        // A culture-aware sort puts "_x" first and ignores case; ordinal order is "B", "_x", "a".
        ExtractedDocument upper = new("B.htm", "Upper", "Upper text.");
        ExtractedDocument underscore = new("_x.htm", "Underscore", "Underscore text.");
        var counter = new FakeTokenCounter();

        DocQaMs.Core.Corpus.Corpus corpus = await Loader(counter).AssembleAsync([Alpha, underscore, upper], Corpora.Ceiling, Ct);

        Assert.Equal(["B.htm", "_x.htm", "a.htm"], corpus.Documents.Select(document => document.FileName));
        Assert.Equal(["Upper", "Underscore", "Part A"], counter.Calls[0].Documents.Select(document => document.Title));
    }

    [Fact]
    public async Task EachDocument_IsSentWithItsLabelAndRenderedContext()
    {
        DocQaMs.Core.Corpus.Corpus corpus = await Corpora.OfAsync(Bravo, Alpha);

        Assert.Equal(
            [
                new RequestDocument("Part A", "Source file: a.htm", Alpha.Text),
                new RequestDocument("Part B", "Source file: b.htm", Bravo.Text),
            ],
            corpus.RequestDocuments);
        Assert.Equal(corpus.RequestDocuments, corpus.Documents.Select(document => document.Request));
    }

    [Fact]
    public async Task TheCorpus_IsCountedWhole_OnEverySelectableModel()
    {
        var counter = new FakeTokenCounter(call => call.IsCorpusCount ? 1_000 + call.Number : 1);

        DocQaMs.Core.Corpus.Corpus corpus = await Loader(counter).AssembleAsync([Alpha, Bravo], Corpora.Ceiling, Ct);

        IReadOnlyList<CountCall> corpusCounts = counter.Calls.Where(call => call.IsCorpusCount).ToList();
        Assert.Equal(CacheAffecting.SelectableModels.Select(model => model.Id), corpusCounts.Select(call => call.ModelId));
        Assert.All(corpusCounts, call => Assert.Equal(corpus.RequestDocuments, call.Documents));
        Assert.Equal(
            CacheAffecting.SelectableModels.Select((model, i) => KeyValuePair.Create(model.Id, 1_001L + i)),
            corpus.TokenCounts.OrderBy(count => count.Key, StringComparer.Ordinal));
        Assert.Equal(1_000L + CacheAffecting.SelectableModels.Count, corpus.LargestTokenCount);
    }

    [Fact]
    public async Task AnEmptyCorpus_IsRejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Loader(new FakeTokenCounter()).AssembleAsync([], Corpora.Ceiling, Ct));

    [Fact]
    public async Task ADuplicateFileName_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Loader(new FakeTokenCounter()).AssembleAsync([Alpha, Alpha with { Label = "Other" }], Corpora.Ceiling, Ct));

        Assert.Contains("more than once: a.htm", ex.Message, StringComparison.Ordinal);
    }

    // --- Size guard ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheSizeGuard_AcceptsACorpusExactlyAtTheCeiling()
    {
        DocQaMs.Core.Corpus.Corpus corpus = await Loader(new FakeTokenCounter(500)).AssembleAsync([Alpha, Bravo], 500, Ct);

        Assert.Equal(500, corpus.LargestTokenCount);
    }

    [Fact]
    public async Task TheSizeGuard_RejectsOneTokenOverTheCeiling_WithoutTruncating()
    {
        var counter = new FakeTokenCounter(501);

        var ex = await Assert.ThrowsAsync<CorpusTooLargeException>(
            () => Loader(counter).AssembleAsync([Alpha, Bravo], 500, Ct));

        Assert.Equal(500, ex.Ceiling);
        Assert.All(CacheAffecting.SelectableModels, model => Assert.Equal(501, ex.TokenCounts[model.Id]));
        Assert.Contains("Nothing was truncated", ex.Message, StringComparison.Ordinal);

        // One whole-corpus count per model, and nothing after: no retry with fewer documents, and no
        // per-document counts for a corpus that is refused.
        Assert.Equal(CacheAffecting.SelectableModels.Count, counter.Calls.Count);
        Assert.All(counter.Calls, call => Assert.Equal(["Part A", "Part B"], call.Documents.Select(document => document.Title)));
    }

    [Fact]
    public async Task TheSizeGuard_AppliesToTheLargestCount_WhenOnlyOneModelIsOver()
    {
        var counter = new FakeTokenCounter(call => call.ModelId == ModelIds.ClaudeSonnet5 ? 501 : 500);

        var ex = await Assert.ThrowsAsync<CorpusTooLargeException>(
            () => Loader(counter).AssembleAsync([Alpha, Bravo], 500, Ct));

        Assert.Equal(500, ex.TokenCounts[ModelIds.ClaudeOpus5]);
        Assert.Equal(501, ex.TokenCounts[ModelIds.ClaudeSonnet5]);
    }

    [Fact]
    public void TheSizeGuardMessage_NamesEveryCount_AndListsTheOptions()
    {
        var ex = new CorpusTooLargeException(
            new Dictionary<string, long> { [ModelIds.ClaudeOpus5] = 401_000, [ModelIds.ClaudeSonnet5] = 400_500 },
            400_000);

        Assert.Contains("401,000 tokens, over the 400,000-token ceiling", ex.Message, StringComparison.Ordinal);
        Assert.Contains("401,000 on claude-opus-5, 400,500 on claude-sonnet-5", ex.Message, StringComparison.Ordinal);
        foreach (string option in new[] { "Remove documents", "Raise DocQaMs:TokenCeiling", "Re-plan" })
        {
            Assert.Contains(option, ex.Message, StringComparison.Ordinal);
        }
    }

    // --- Per-document counts ------------------------------------------------------------------------

    [Fact]
    public async Task EachDocument_IsCountedAlone_OnTheDefaultModel()
    {
        var counter = new FakeTokenCounter();

        DocQaMs.Core.Corpus.Corpus corpus = await Loader(counter).AssembleAsync([Alpha, Bravo], Corpora.Ceiling, Ct);

        IReadOnlyList<CountCall> perDocument = counter.Calls.Where(call => !call.IsCorpusCount).ToList();
        Assert.All(perDocument, call => Assert.Equal(CacheAffecting.DefaultModelId, call.ModelId));
        Assert.Equal(
            corpus.RequestDocuments.Select(document => new[] { document }),
            perDocument.Select(call => call.Documents));
        Assert.Equal([(long?)Alpha.Text.Length, Bravo.Text.Length], corpus.Documents.Select(document => document.ApproximateTokens));
    }

    [Fact]
    public async Task AFailedPerDocumentCount_DoesNotStopTheLoad()
    {
        var counter = new FakeTokenCounter(call => call.Number switch
        {
            _ when call.IsCorpusCount => 1_000,
            // The first per-document count follows one corpus count per model.
            _ when call.Number == CacheAffecting.SelectableModels.Count + 1 => 11,
            _ => throw new HttpRequestException("rate limited"),
        });

        DocQaMs.Core.Corpus.Corpus corpus = await Loader(counter).AssembleAsync([Alpha, Bravo, Charlie], Corpora.Ceiling, Ct);

        // The first failure ends the counting: the calls after it would most likely fail the same way.
        Assert.Equal([(long?)11, null, null], corpus.Documents.Select(document => document.ApproximateTokens));
        Assert.Equal(CacheAffecting.SelectableModels.Count + 2, counter.Calls.Count);
        Assert.Equal(1_000, corpus.LargestTokenCount);
    }

    [Fact]
    public async Task Cancellation_DuringThePerDocumentCounts_IsNotSwallowed()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var counter = new FakeTokenCounter(call =>
        {
            if (!call.IsCorpusCount)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }

            return 1_000;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Loader(counter).AssembleAsync([Alpha, Bravo], Corpora.Ceiling, cts.Token));
    }

    // --- The snapshot -------------------------------------------------------------------------------

    [Fact]
    public async Task ParagraphRanges_CoverEachParagraph_AndNotTheBlankLineBetween()
    {
        DocQaMs.Core.Corpus.Corpus corpus = await Corpora.OfAsync(Alpha);
        CorpusDocument document = corpus.Documents[0];

        Assert.Equal([new TextRange(0, 11), new TextRange(13, 22)], document.Paragraphs);
        Assert.Equal(["Alpha text.", "a) First."], document.Paragraphs.Select(range => document.Text[range.Start..range.End]));
    }

    [Fact]
    public async Task CharacterCounts_AreInCodePoints()
    {
        string clef = char.ConvertFromUtf32(0x1D11E);
        DocQaMs.Core.Corpus.Corpus corpus = await Corpora.OfAsync(new ExtractedDocument("a.htm", "Part A", $"Music {clef} notation."));
        CorpusDocument document = corpus.Documents[0];

        Assert.Equal(document.Text.Length - 1, document.CharacterCount);
        Assert.False(document.CodePoints.IsIdentity);
    }

    // --- Fingerprint --------------------------------------------------------------------------------

    [Fact]
    public async Task IdenticalInput_GivesAnIdenticalFingerprint()
    {
        DocQaMs.Core.Corpus.Corpus first = await Corpora.OfAsync(Alpha, Bravo);

        // Equal but distinct objects, supplied in the other order and with other token counts: neither the
        // order given nor the count is part of what is sent.
        DocQaMs.Core.Corpus.Corpus second = await Corpora.OfAsync(2_000, Bravo with { }, Alpha with { });

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", first.Fingerprint);
    }

    public static TheoryData<string, ExtractedDocument[]> Changes => new()
    {
        { "text", [Alpha with { Text = Alpha.Text + " " }, Bravo] },
        { "label", [Alpha with { Label = "Part A2" }, Bravo] },
        { "file name, which renders into the context", [Alpha with { FileName = "a2.htm" }, Bravo] },
        { "added", [Alpha, Bravo, Charlie] },
        { "removed", [Alpha] },
        { "breakpoint moves to another block", [Bravo, Alpha with { FileName = "z.htm" }] },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AnyChangeToWhatIsSent_ChangesTheFingerprint(string change, ExtractedDocument[] documents)
    {
        DocQaMs.Core.Corpus.Corpus baseline = await Corpora.OfAsync(Alpha, Bravo);
        DocQaMs.Core.Corpus.Corpus changed = await Corpora.OfAsync(documents);

        Assert.True(baseline.Fingerprint != changed.Fingerprint, $"A change of {change} left the fingerprint unchanged.");
    }

    [Fact]
    public void TheFingerprint_IsTheHashOfTheSystemPromptAndTheCanonicalBlocks()
    {
        RequestDocument[] documents = [new("Part A", "Source file: a.htm", "Alpha.")];
        const string Blocks =
            """[{"type":"document","source":{"type":"text","media_type":"text/plain","data":"Alpha."},"title":"Part A","context":"Source file: a.htm","citations":{"enabled":true},"cache_control":{"type":"ephemeral","ttl":"1h"}}]""";

        Assert.Equal(Blocks, System.Text.Encoding.UTF8.GetString(CorpusFingerprint.CanonicalJson(documents)));
        Assert.Equal(
            $$"""{"system":"Answer.","documents":{{Blocks}}}""",
            System.Text.Encoding.UTF8.GetString(CorpusFingerprint.CanonicalPrefixJson("Answer.", documents)));
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                CorpusFingerprint.CanonicalPrefixJson(CacheAffecting.SystemPrompt, documents))),
            CorpusFingerprint.Compute(documents));
    }

    [Fact]
    public void AChangeToTheSystemPrompt_ChangesTheFingerprint()
    {
        // The system prompt renders before the documents, so editing it invalidates every cached corpus copy.
        RequestDocument[] documents = [new("Part A", "Source file: a.htm", "Alpha.")];

        Assert.NotEqual(
            CorpusFingerprint.Compute(CacheAffecting.SystemPrompt, documents),
            CorpusFingerprint.Compute($"{CacheAffecting.SystemPrompt} ", documents));
    }

    private static CorpusLoader Loader(FakeTokenCounter counter) => new(counter, NullLogger<CorpusLoader>.Instance);
}
