using System.Globalization;
using System.Text.Json.Nodes;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Plan item 3: the state file. A missing, unreadable or malformed file, including a field of the wrong
/// type, is reported, never thrown.
/// </summary>
public sealed class CorpusStateFileTests : IDisposable
{
    private const string Parser = "1.8.1";

    private static readonly ExtractedDocument Alpha = new("a.htm", "Part A", "Alpha text.\n\na) First.");
    private static readonly ExtractedDocument Bravo = new("b.htm", "Part B", "Bravo text.");
    private static readonly ExtractedDocument Charlie = new("c.htm", "Part C", "Charlie.");

    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 14, 12, 30, 0, TimeSpan.FromHours(-5));

    private readonly TempDirectory _folder = new();

    private string StatePath => _folder.Combine("state.json");

    public void Dispose() => _folder.Dispose();

    // --- Compare and record -------------------------------------------------------------------------

    [Fact]
    public async Task AMissingFile_IsAFirstRun()
    {
        DocQaMs.Core.Corpus.Corpus corpus = await Corpora.OfAsync(Alpha);

        Assert.Equal(FingerprintStatus.FirstRun, CorpusStateFile.Compare(corpus, StatePath, Parser).Status);
        Assert.Equal(FingerprintStatus.FirstRun, CorpusStateFile.Compare(corpus, _folder.Combine("no-such-folder", "state.json"), Parser).Status);
    }

    [Fact]
    public async Task ARecordedFingerprint_ComparesUnchanged_AndLeavesNoTemporaryFile()
    {
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha, Bravo), StatePath, Parser, RecordedAt);

        FingerprintCheck check = CorpusStateFile.Compare(await Corpora.OfAsync(Alpha, Bravo), StatePath, Parser);

        Assert.Equal(FingerprintStatus.Unchanged, check.Status);
        Assert.Equal("2026-09-14T17:30:00+00:00", check.PreviousRecordedAt);
        Assert.Empty(check.Details);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public async Task TheFile_RecordsEveryField()
    {
        string clef = char.ConvertFromUtf32(0x1D11E);
        DocQaMs.Core.Corpus.Corpus corpus = await Corpora.OfAsync(199_749, new ExtractedDocument("a.htm", "Part A", $"Music {clef}."));

        CorpusStateFile.Record(corpus, StatePath, Parser, RecordedAt);

        JsonObject state = JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        Assert.Equal(1, (int?)state["version"]);
        Assert.Equal("2026-09-14T17:30:00+00:00", (string?)state["recorded_at"]);
        Assert.Equal(corpus.Fingerprint, (string?)state["fingerprint"]);
        Assert.Equal(Parser, (string?)state["anglesharp_version"]);
        Assert.All(CacheAffecting.SelectableModels, model => Assert.Equal(199_749, (long?)state["tokens"]![model.Id]));
        JsonTest.Equal(
            $$"""[{"filename":"a.htm","label":"Part A","chars":8,"text_sha256":"{{CorpusStateFile.Sha256($"Music {clef}.")}}"}]""",
            state["documents"]);
    }

    [Fact]
    public async Task ChangedText_NamesTheDocumentAndTheTokenChangeOnEachModel()
    {
        CorpusStateFile.Record(await Corpora.OfAsync(1_000, Alpha, Bravo), StatePath, Parser, RecordedAt);
        DocQaMs.Core.Corpus.Corpus changed = await Corpora.OfAsync(1_005, Alpha, Bravo with { Text = "Bravo text, amended." });

        FingerprintCheck check = CorpusStateFile.Compare(changed, StatePath, Parser);

        Assert.Equal(FingerprintStatus.Changed, check.Status);
        Assert.Equal(
            [
                "b.htm: text changed (11 -> 20 characters)",
                "tokens on claude-opus-5: 1,000 -> 1,005",
                "tokens on claude-opus-5-5: 1,000 -> 1,005",
                "tokens on claude-sonnet-5: 1,000 -> 1,005",
            ],
            check.Details);
    }

    [Fact]
    public async Task AChangedLabel_AndAddedAndRemovedFiles_AreEachNamed()
    {
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha, Bravo), StatePath, Parser, RecordedAt);
        DocQaMs.Core.Corpus.Corpus changed = await Corpora.OfAsync(Alpha with { Label = "Part A2" }, Charlie);

        FingerprintCheck check = CorpusStateFile.Compare(changed, StatePath, Parser);

        Assert.Equal(
            ["c.htm: added", "b.htm: removed", "a.htm: label changed (\"Part A\" -> \"Part A2\")"],
            check.Details);
    }

    [Fact]
    public async Task AnAngleSharpUpgrade_IsNamedAsTheLikelyCause()
    {
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha), StatePath, "1.8.1", RecordedAt);
        DocQaMs.Core.Corpus.Corpus reExtracted = await Corpora.OfAsync(Alpha with { Text = "Alpha text, re-extracted." });

        FingerprintCheck check = CorpusStateFile.Compare(reExtracted, StatePath, "1.9.0");

        Assert.Contains(check.Details, detail => detail.StartsWith("AngleSharp 1.8.1 -> 1.9.0: the likely cause", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABlockSettingChange_IsReported_WhenNoDocumentChanged()
    {
        // Stands in for an edit to the system prompt, the context template or the TTL: the documents are the
        // same, but the recorded fingerprint is not this corpus's.
        DocQaMs.Core.Corpus.Corpus corpus = await Corpora.OfAsync(Alpha);
        CorpusStateFile.Record(corpus, StatePath, Parser, RecordedAt);
        EditState(state => state["fingerprint"] = new string('0', 64));

        FingerprintCheck check = CorpusStateFile.Compare(corpus, StatePath, Parser);

        Assert.Equal(FingerprintStatus.Changed, check.Status);
        Assert.StartsWith("no document's text or label changed, so the system prompt or a block setting did", check.Details[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewlySelectableModel_IsNamedAsFirstCounted()
    {
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha), StatePath, Parser, RecordedAt);
        EditState(state => state["tokens"]!.AsObject().Remove(ModelIds.ClaudeSonnet5));

        FingerprintCheck check = CorpusStateFile.Compare(await Corpora.OfAsync(Alpha with { Text = "Changed." }), StatePath, Parser);

        Assert.Contains("tokens on claude-sonnet-5: not counted -> 1,000", check.Details);
    }

    // --- Unreadable files ---------------------------------------------------------------------------

    public static TheoryData<string, string> UnreadableContents => new()
    {
        { "invalid JSON", "{not json" },
        { "not an object", "[]" },
        { "unknown version", """{"version": 99}""" },
        { "version as a string", """{"version": "1"}""" },
        { "missing fields", """{"version": 1, "fingerprint": "abc"}""" },
        { "empty", "" },
    };

    [Theory]
    [MemberData(nameof(UnreadableContents))]
    public async Task AnUnreadableFile_IsReported_NotThrown(string kind, string content)
    {
        File.WriteAllText(StatePath, content);

        FingerprintCheck check = CorpusStateFile.Compare(await Corpora.OfAsync(Alpha), StatePath, Parser);

        Assert.True(check.Status == FingerprintStatus.Unreadable, $"A state file with {kind} was not reported as unreadable.");
        Assert.Single(check.Details);
        Assert.StartsWith(StatePath, check.Details[0], StringComparison.Ordinal);
    }

    public static TheoryData<string, string> MistypedFields => new()
    {
        { "chars", "\"oops\"" },
        { "chars", "1.5" },
        { "filename", "[\"a.htm\"]" },
        { "label", "null" },
        { "text_sha256", "5" },
    };

    [Theory]
    [MemberData(nameof(MistypedFields))]
    public async Task AMistypedDocumentField_IsReported_NotThrown(string field, string json)
    {
        // A hand-edited file: every key present, one of the wrong type. The changed text makes the
        // comparison describe the document, which is where a wrong type would throw if it got that far.
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha), StatePath, Parser, RecordedAt);
        EditState(state => state["documents"]![0]![field] = JsonNode.Parse(json));

        FingerprintCheck check = CorpusStateFile.Compare(await Corpora.OfAsync(Alpha with { Text = "Alpha, amended." }), StatePath, Parser);

        Assert.Equal(FingerprintStatus.Unreadable, check.Status);
    }

    public static TheoryData<string, string> MistypedTopLevelFields => new()
    {
        { "tokens", "[]" },
        { "tokens", """{"claude-opus-5": "many"}""" },
        { "fingerprint", "42" },
        { "recorded_at", "null" },
        { "anglesharp_version", "{}" },
        { "documents", "{}" },
    };

    [Theory]
    [MemberData(nameof(MistypedTopLevelFields))]
    public async Task AMistypedTopLevelField_IsReported_NotThrown(string field, string json)
    {
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha), StatePath, Parser, RecordedAt);
        EditState(state => state[field] = JsonNode.Parse(json));

        Assert.Equal(FingerprintStatus.Unreadable, CorpusStateFile.Compare(await Corpora.OfAsync(Alpha), StatePath, Parser).Status);
    }

    [Fact]
    public async Task AFileNamingADocumentTwice_IsReported_NotThrown()
    {
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha), StatePath, Parser, RecordedAt);
        EditState(state => state["documents"]!.AsArray().Add(state["documents"]![0]!.DeepClone()));

        FingerprintCheck check = CorpusStateFile.Compare(await Corpora.OfAsync(Alpha with { Text = "Changed." }), StatePath, Parser);

        Assert.Equal(FingerprintStatus.Unreadable, check.Status);
    }

    [Fact]
    public async Task AStatePathThatCannotBeRead_IsReported_NotThrown()
    {
        Directory.CreateDirectory(StatePath);

        FingerprintCheck check = CorpusStateFile.Compare(await Corpora.OfAsync(Alpha), StatePath, Parser);

        Assert.Equal(FingerprintStatus.Unreadable, check.Status);
    }

    [Fact]
    public async Task Recording_ReplacesAnUnreadableFile()
    {
        File.WriteAllText(StatePath, "{not json");

        CorpusStateFile.Record(await Corpora.OfAsync(Alpha), StatePath, Parser, RecordedAt);

        Assert.Equal(FingerprintStatus.Unchanged, CorpusStateFile.Compare(await Corpora.OfAsync(Alpha), StatePath, Parser).Status);
    }

    // --- Describe -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheChangeWarning_NamesTheCause_AndTheWriteCostOnEachModel()
    {
        DocQaOptions options = TestSettings.ValidOptions();
        CorpusStateFile.Record(await Corpora.OfAsync(Alpha), StatePath, Parser, RecordedAt);
        DocQaMs.Core.Corpus.Corpus changed = await Corpora.OfAsync(200_000, Alpha with { Text = "Alpha text, amended." });

        string warning = CorpusStateFile.Compare(changed, StatePath, Parser).Describe(changed, options.Prices);

        Assert.StartsWith("The corpus changed since the load recorded at 2026-09-14T17:30:00+00:00:", warning, StringComparison.Ordinal);
        Assert.Contains("\n  - a.htm: text changed", warning, StringComparison.Ordinal);
        Assert.Contains("an AngleSharp upgrade", warning, StringComparison.Ordinal);

        // Recomputed from the price table, not copied from the plan.
        string costs = string.Join(" and ", CacheAffecting.SelectableModels.Select(model => string.Create(
            CultureInfo.InvariantCulture,
            $"${200_000m * options.Prices[model.Id].CacheWrite1hPerMTok / 1_000_000m:F2} on {model.DisplayName}")));
        Assert.EndsWith($"about {costs}.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOtherOutcomes_AreDescribedInOneLine()
    {
        DocQaMs.Core.Corpus.Corpus corpus = await Corpora.OfAsync(Alpha);
        DocQaOptions options = TestSettings.ValidOptions();

        Assert.Equal(
            "No previous fingerprint on record; this one is now recorded.",
            CorpusStateFile.Compare(corpus, StatePath, Parser).Describe(corpus, options.Prices));

        File.WriteAllText(StatePath, "[]");
        Assert.StartsWith(
            "The previous fingerprint could not be read (",
            CorpusStateFile.Compare(corpus, StatePath, Parser).Describe(corpus, options.Prices),
            StringComparison.Ordinal);

        CorpusStateFile.Record(corpus, StatePath, Parser, RecordedAt);
        Assert.Equal(
            "Unchanged since the load recorded at 2026-09-14T17:30:00+00:00.",
            CorpusStateFile.Compare(corpus, StatePath, Parser).Describe(corpus, options.Prices));
    }

    private void EditState(Action<JsonObject> edit)
    {
        JsonObject state = JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        edit(state);
        File.WriteAllText(StatePath, state.ToJsonString());
    }
}
