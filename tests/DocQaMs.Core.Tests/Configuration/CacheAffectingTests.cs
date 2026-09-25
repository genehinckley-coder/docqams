using System.Security.Cryptography;
using System.Text;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Tests.Configuration;

/// <summary>
/// Pins every cache-affecting value. A failure means an edit that cold-starts corpus cache entries:
/// about $2.00 on Opus 5 and $0.80 on Sonnet 5 for the next question on each affected combination.
/// If the edit is deliberate, update the pinned hash.
/// </summary>
public sealed class CacheAffectingTests
{
    [Fact]
    public void SystemPrompt_IsPinned() =>
        // Diverged from the prototype's SYSTEM_PROMPT (tests/test_answer.py) by the addition of the
        // Summary/Detail answer structure, so this hash is ours, not the prototype's.
        Assert.Equal(
            "f64485c3edbb60236e35bf5f931b8023ba0b493d048729dcdad9ce9ba4da80d1",
            Sha256(CacheAffecting.SystemPrompt));

    [Fact]
    public void DocumentLabels_AreThePrototypesVerbatim()
    {
        // "file<TAB>label<LF>" per entry, in ordinal file-name order. The prototype's DOCUMENT_LABELS,
        // serialized the same way, hash to the same value.
        string canonical = string.Concat(
            CacheAffecting.DocumentLabels
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}\t{pair.Value}\n"));

        Assert.Equal("5d05d2feb0338f5c7ef0c63389660ee2a75577e41d0e9057bf59baa108737466", Sha256(canonical));
    }

    [Fact]
    public void DocumentContextTemplate_IsThePrototypesVerbatim() =>
        Assert.Equal(
            "c6c2bdef2efa389b128e94e75e4a9966e8f37cd929453961b0db50c83dc95485",
            Sha256(CacheAffecting.DocumentContextTemplate));

    [Fact]
    public void CacheTtls_ArePinned() =>
        Assert.Equal(
            "f39ea10670925587fc340eded22d4bd5d2a1c23c7f22f46d3509c9d29b8861d6",
            Sha256($"corpus={CacheAffecting.CorpusCacheTtl}\nconversation={CacheAffecting.ConversationCacheTtl}\n"));

    [Fact]
    public void ThinkingConfiguration_IsPinned() =>
        Assert.Equal(
            "92de0a748c954c0ded9dd4d9d2a685be6ccc0d7237af9fe50e5d322717a9a6a4",
            Sha256($"type={CacheAffecting.ThinkingType}\ndisplay={CacheAffecting.ThinkingDisplay}\n"));

    [Fact]
    public void SelectableModels_ArePinned() =>
        Assert.Equal(
            "fbdd74271f67c3ade4382e7b327fef2353e306c699ae31f4b6415c33e39cb4b0",
            Sha256(string.Concat(CacheAffecting.SelectableModels.Select(model => $"{model.Id}\n"))));

    [Fact]
    public void EffortLevels_ArePinned() =>
        Assert.Equal(
            "f1eedefed99fb647b2d989b7df92b77b83dc4d4564289fcfb06fd3cbc2475ebe",
            Sha256(string.Concat(CacheAffecting.EffortLevels.Select(level => $"{level.ToWireValue()}\n"))));

    [Fact]
    public void ExcludedCombinations_ArePinned() =>
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Sha256(string.Concat(CacheAffecting.ExcludedCombinations.Select(
                excluded => $"{excluded.ModelId}\t{excluded.Effort.ToWireValue()}\t{excluded.Reason}\n"))));

    [Fact]
    public void DefaultCombination_IsPinned() =>
        Assert.Equal(
            "d88e139200e3e2089a0df9d3a7816bec2720217adea60f80bea55f24dfd16a7d",
            Sha256($"{CacheAffecting.DefaultModelId}\t{CacheAffecting.DefaultEffort.ToWireValue()}\n"));

    [Fact]
    public void DefaultCombination_IsSelectableAndNotExcluded()
    {
        Assert.Contains(CacheAffecting.SelectableModels, model => model.Id == CacheAffecting.DefaultModelId);
        Assert.Contains(CacheAffecting.DefaultEffort, CacheAffecting.EffortLevels);
        Assert.DoesNotContain(
            CacheAffecting.ExcludedCombinations,
            excluded => excluded.ModelId == CacheAffecting.DefaultModelId && excluded.Effort == CacheAffecting.DefaultEffort);
    }

    [Fact]
    public void ExcludedCombinations_ReferOnlyToSelectableModelsAndLevels_AndGiveAReason() =>
        Assert.All(CacheAffecting.ExcludedCombinations, excluded =>
        {
            Assert.Contains(CacheAffecting.SelectableModels, model => model.Id == excluded.ModelId);
            Assert.Contains(excluded.Effort, CacheAffecting.EffortLevels);
            Assert.False(string.IsNullOrWhiteSpace(excluded.Reason));
        });

    [Fact]
    public void EffortLevels_CoverEveryEnumValueInOrder() =>
        Assert.Equal(Enum.GetValues<EffortLevel>(), CacheAffecting.EffortLevels);

    [Fact]
    public void ToWireValue_RejectsAnUndefinedLevel() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EffortLevel)99).ToWireValue());

    [Fact]
    public void RenderDocumentContext_FillsInTheFileName() =>
        Assert.Equal(
            "Source file: ADMINISTRATIVE CODE 41 175.htm",
            CacheAffecting.RenderDocumentContext("ADMINISTRATIVE CODE 41 175.htm"));

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
