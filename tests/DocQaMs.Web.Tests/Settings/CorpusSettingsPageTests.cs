using AngleSharp.Dom;
using Bunit;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Web.Components.Pages;
using DocQaMs.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;
using static DocQaMs.Web.Tests.TestSupport.FakeConversationStore;

namespace DocQaMs.Web.Tests.Settings;

/// <summary>Plan item 10: the read-only Corpus settings screen, against a fake store, corpus and clock.</summary>
public sealed class CorpusSettingsPageTests : IDisposable
{
    private const decimal PerMillion = 1_000_000m;

    private readonly BunitContext _context = new();
    private readonly FakeConversationStore _store = new();
    private readonly FakeCorpusSource _corpus = new(FakeCorpusSource.Ready(TestData.Corpus));
    private readonly FakeTimeProvider _time = new(TestData.Start);
    private readonly DocQaOptions _options = TestData.Options();
    private readonly CacheWarmthTracker _warmth;

    public CorpusSettingsPageTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _warmth = new CacheWarmthTracker(_time);
        _context.Services.AddSingleton<IConversationStore>(_store);
        _context.Services.AddSingleton<ICorpusSource>(_corpus);
        _context.Services.AddSingleton(_warmth);
        _context.Services.AddSingleton(new CostEstimator(Options.Create(_options)));
        _context.Services.AddSingleton(Options.Create(_options));
        _context.Services.AddSingleton<TimeProvider>(_time);
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    public void Dispose() => _context.Dispose();

    private NavigationManager Navigation => _context.Services.GetRequiredService<NavigationManager>();

    // --- Corpus health ------------------------------------------------------------------------------

    [Fact]
    public void TheGauge_MeasuresEachModelsOwnCount_AgainstTheCeiling()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        IReadOnlyList<IElement> gauges = cut.FindAll(".set__gauge");
        Assert.Equal(CacheAffecting.SelectableModels.Count, gauges.Count);

        // 199,745 of a 400,000 ceiling is 50%, computed here from the options rather than read off the screen.
        string share = Formatting.Percent((double)TestData.Tokens / _options.TokenCeiling);
        Assert.Equal(
            $"Claude Opus 5 {TestData.Tokens:N0} tokens / {_options.TokenCeiling:N0} ceiling",
            Flat(gauges[0].QuerySelector(".set__gauge-head")!));
        Assert.Equal($"{share} OF CEILING", gauges[0].QuerySelector(".set__gauge-label")!.TextContent);
        Assert.Contains($"width: {share}", gauges[0].QuerySelector(".set__gauge-fill")!.GetAttribute("style"), StringComparison.Ordinal);
    }

    [Fact]
    public void AModelWithNoCount_SaysSo_InsteadOfShowingAShare()
    {
        CorpusSnapshot corpus = new(
            TestData.Corpus.Documents,
            "fingerprint-1",
            new Dictionary<string, long> { [ModelIds.ClaudeOpus5] = TestData.Tokens });
        _corpus.Publish(FakeCorpusSource.Ready(corpus));

        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        IElement sonnet = cut.FindAll(".set__gauge")[1];
        Assert.Contains("tokens not counted", sonnet.QuerySelector(".set__gauge-head")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("UNKNOWN SHARE OF CEILING", sonnet.QuerySelector(".set__gauge-label")!.TextContent);

        // Its cost rows are priced on the largest count, so the cache card says so rather than contradict the gauge.
        Assert.Contains(
            "Claude Sonnet 5 has no token count of its own, so its rows are priced on the largest count",
            Flat(cut.Find(".set__card--cache")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedCorpus_ShowsItsStatus_ItsDetails_ItsFingerprintAndWhenItWasChecked()
    {
        _corpus.Publish(new CorpusState.Ready(
            TestData.Corpus,
            new FingerprintCheck(FingerprintStatus.Changed, "2026-09-13T10:00:00Z", ["Part 175 changed"]),
            "The corpus changed since the load recorded at 2026-09-13T10:00:00Z:\n  - Part 175 changed",
            TestData.Start));

        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();
        IElement card = cut.Find(".set__card--corpus");

        Assert.Equal("CORPUS CHANGED", card.QuerySelector(".d1-badge")!.TextContent.Trim());
        Assert.Contains("Part 175 changed", card.QuerySelector(".set__prose")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("SHA-256 fingerprint-1", card.QuerySelector(".set__mono")!.TextContent);
        Assert.Contains("Checked at 2026-09-14 12:00", card.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_AsksTheCorpusSourceOnce_AndSaysSoWhileItRuns()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        cut.Find(".set__card--corpus .d1-btn").Click();
        Assert.Equal(1, _corpus.Reloads);

        _corpus.Publish(FakeCorpusSource.Ready(TestData.Corpus) with { IsReloading = true });
        IElement button = cut.Find(".set__card--corpus .d1-btn");
        Assert.Equal("Reloading…", button.TextContent.Trim());

        // aria-disabled, not disabled, so the button the user just pressed keeps keyboard focus.
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));
        button.Click();
        Assert.Equal(1, _corpus.Reloads);
    }

    [Fact]
    public void AFailedReload_IsShown_AndTheCorpusLoadedBeforeStaysOnScreen()
    {
        _corpus.Publish(FakeCorpusSource.Ready(TestData.Corpus) with { ReloadFailure = "The corpus folder is empty." });

        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        Assert.Contains("RELOAD FAILED", cut.Find(".set__card--corpus").TextContent, StringComparison.Ordinal);
        Assert.Contains("The corpus folder is empty.", cut.Find(".set__card--corpus").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll(".set__gauge"));
    }

    [Fact]
    public void WithNoCorpus_EachCardSaysWhatIsMissing_AndRetryReloads()
    {
        _corpus.Publish(new CorpusState.Failed("The API key file is missing.", TestData.Start));

        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        Assert.Contains("CORPUS NOT LOADED", cut.Find(".set__card--corpus").TextContent, StringComparison.Ordinal);
        Assert.Contains("The API key file is missing.", cut.Find(".set__card--corpus").TextContent, StringComparison.Ordinal);
        Assert.Contains("appear here once the corpus has loaded", cut.Find(".set__card--registry").TextContent, StringComparison.Ordinal);
        Assert.Contains("once the corpus has loaded and been counted", cut.Find(".set__card--cache").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".set__gauge"));

        cut.Find(".set__card--corpus .d1-btn").Click();
        Assert.Equal(1, _corpus.Reloads);
    }

    [Fact]
    public void WhileTheCorpusLoads_TheScreenSaysSo()
    {
        _corpus.Publish(new CorpusState.Loading());

        Assert.Contains("CORPUS LOADING", _context.Render<CorpusSettings>().Find(".set__card--corpus").TextContent, StringComparison.Ordinal);
    }

    // --- Registry -----------------------------------------------------------------------------------

    [Fact]
    public void TheRegistry_ListsEachDocumentReadOnly_WithItsFileNameLabelAndCounts()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        IElement row = Assert.Single(cut.FindAll(".set__registry-row"));
        CorpusDocument document = TestData.Corpus.Documents[0];
        Assert.Equal(document.FileName, row.QuerySelector(".set__eyebrow")!.TextContent);
        Assert.Equal(TestData.Label, row.QuerySelector(".set__registry-label")!.TextContent);
        Assert.Equal($"{document.CharacterCount:N0} c · 1,000 t", row.QuerySelector(".set__registry-counts")!.TextContent);

        // The design's editable label inputs are values here: an edit would cost a fresh corpus write per combination.
        Assert.Empty(cut.FindAll("input"));
        Assert.Empty(cut.FindAll("select"));
    }

    [Fact]
    public void TheRegistryNote_NamesTheModelTheDocumentsWereCountedOn()
    {
        // The loader counts each document on the default model, and the tokenizers differ slightly.
        string modelName = CacheAffecting.SelectableModels.Single(model => model.Id == CacheAffecting.DefaultModelId).DisplayName;

        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        Assert.Contains(
            $"Token counts are approximate, counted on {modelName}:",
            System.Text.RegularExpressions.Regex.Replace(cut.Find(".set__card--registry .set__note").TextContent, @"\s+", " "),
            StringComparison.Ordinal);
    }

    // --- Cache and pricing --------------------------------------------------------------------------

    [Fact]
    public void TheCacheCard_ShowsTheTtlsAndTheCeiling_FromTheValuesTheRequestUses()
    {
        string card = Flat(_context.Render<CorpusSettings>().Find(".set__card--cache"));

        Assert.Contains("Corpus cache TTL " + CacheAffecting.CorpusCacheTtl, card, StringComparison.Ordinal);
        Assert.Contains("Conversation cache TTL " + CacheAffecting.ConversationCacheTtl, card, StringComparison.Ordinal);
        Assert.Contains($"Token ceiling {_options.TokenCeiling:N0}", card, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePriceTable_ShowsEveryBucketOfBothModels_AndSaysThereIsNoLongContextSurcharge()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        IReadOnlyList<IElement> rows = Rows(cut, "prices-heading");
        Assert.Equal(CacheAffecting.SelectableModels.Count, rows.Count);
        foreach ((SelectableModel model, IElement row) in CacheAffecting.SelectableModels.Zip(rows))
        {
            ModelPrices prices = _options.Prices[model.Id];
            Assert.Equal(model.DisplayName, row.QuerySelector("th")!.TextContent);
            decimal[] rates =
                [prices.InputPerMTok, prices.CacheWrite5mPerMTok, prices.CacheWrite1hPerMTok, prices.CacheReadPerMTok, prices.OutputPerMTok];
            Assert.Equal(
                rates.Select(Formatting.Money),
                row.QuerySelectorAll("td").Select(cell => cell.TextContent));
        }

        Assert.Contains("no long-context surcharge", cut.Find(".set__card--cache").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCostTable_HasARowPerCombination_WithFiguresComputedFromThePricesAndOutputRanges()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        IReadOnlyList<IElement> rows = Rows(cut, "per-question-heading");
        Assert.Equal(CacheAffecting.SelectableModels.Count * CacheAffecting.EffortLevels.Count, rows.Count);

        // Opus 5 at High, a measured combination, recomputed here from the options themselves.
        ModelPrices prices = _options.Prices[ModelIds.ClaudeOpus5];
        OutputTokenRange output = _options.MeasuredOutputTokens[ModelIds.ClaudeOpus5][EffortLevel.High];
        decimal write = TestData.Tokens * prices.CacheWrite1hPerMTok / PerMillion;
        decimal read = TestData.Tokens * prices.CacheReadPerMTok / PerMillion;
        decimal tail = output.Highest * prices.CacheWrite5mPerMTok / PerMillion;
        string cold = Formatting.Money(new CostRange(
            write + (output.Lowest * prices.OutputPerMTok / PerMillion),
            write + (output.Highest * prices.OutputPerMTok / PerMillion)));
        string warm = Formatting.Money(new CostRange(
            read + (output.Lowest * prices.OutputPerMTok / PerMillion),
            read + tail + (output.Highest * prices.OutputPerMTok / PerMillion)));

        IElement row = rows[LevelIndex(EffortLevel.High)];
        Assert.Equal("Opus 5, High", row.QuerySelector("th")!.TextContent);
        Assert.Equal([cold, warm, "Measured", "Cold"], row.QuerySelectorAll("td").Select(cell => cell.TextContent));
    }

    [Fact]
    public void EachRowsBasis_SaysHowFarItsOutputFiguresCanBeTrusted()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        IEnumerable<string> bases = Rows(cut, "per-question-heading")
            .Select(row => row.QuerySelectorAll("td")[2].TextContent);
        Assert.Equal(
            [
                "Estimate", "Estimate", "Measured", "Assumption", "Assumption",
                "Measured", "Measured", "Measured", "Assumption", "Assumption",
                "Estimate", "Estimate", "Assumption", "Assumption", "Assumption",
            ],
            bases);
    }

    [Fact]
    public void TheWorstCaseTable_ShowsEachLevelsOutputLimit_AndBothCappedFigures()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        IElement row = Rows(cut, "worst-case-heading")[LevelIndex(EffortLevel.Max)];
        ModelPrices prices = _options.Prices[ModelIds.ClaudeOpus5];
        int limit = _options.MaxTokens[EffortLevel.Max];
        decimal capped = limit * prices.OutputPerMTok / PerMillion;
        decimal read = TestData.Tokens * prices.CacheReadPerMTok / PerMillion;

        Assert.Equal("Opus 5, Max", row.QuerySelector("th")!.TextContent);
        Assert.Equal(
            [
                limit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                Formatting.Money((TestData.Tokens * prices.CacheWrite1hPerMTok / PerMillion) + capped),
                Formatting.Money(read + (limit * prices.CacheWrite5mPerMTok / PerMillion) + capped),
            ],
            row.QuerySelectorAll("td").Select(cell => cell.TextContent));
    }

    [Fact]
    public void TheCacheColumn_ReadsTheWarmthTracker_AndTurnsColdWhenTheEntryExpires()
    {
        // Opus 5, whose rows come first, so CacheCell reads them.
        AnswerSettings settings = AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.High);
        _warmth.RecordUsage(CorpusCacheKey.For(TestData.Corpus, settings), new TokenUsage(0, TestData.Tokens, 0, 0, 0));

        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();
        DateTimeOffset warmUntil = TestData.Start + CacheWarmthTracker.CorpusTtl - CacheWarmthTracker.ExpiryMargin;
        Assert.Equal($"Probably warm until {Formatting.Time(warmUntil, TimeZoneInfo.Utc)}", CacheCell(cut, EffortLevel.High));
        Assert.Equal("Cold", CacheCell(cut, EffortLevel.Low));

        // The prediction turns cold at a known time with nothing else changing, so the screen refreshes itself then.
        _time.Advance(CacheWarmthTracker.CorpusTtl);
        cut.WaitForAssertion(() => Assert.Equal("Cold", CacheCell(cut, EffortLevel.High)));
    }

    [Fact]
    public void ACombinationAnotherConversationIsWriting_SaysSo_SoASecondWriteIsNotPaidFor()
    {
        _warmth.BeginRequest(CorpusCacheKey.For(TestData.Corpus, AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.High)));

        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        Assert.Equal("Cold; a write is in progress", CacheCell(cut, EffortLevel.High));
        Assert.Equal("Cold", CacheCell(cut, EffortLevel.Low));
    }

    [Fact]
    public void TheOpenConversationsOwnCombination_IsMarkedInBothTables()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id) with { Settings = TestData.Sonnet(EffortLevel.Low) });

        IRenderedComponent<CorpusSettings> cut = RenderAt(id);

        foreach (string heading in new[] { "per-question-heading", "worst-case-heading" })
        {
            IElement marked = Assert.Single(Rows(cut, heading), row => row.ClassList.Contains("set__table-row--current"));
            Assert.Equal("Sonnet 5, Low", marked.QuerySelector("th")!.TextContent);
        }
    }

    [Fact]
    public void EachTable_IsNamedByItsHeading_SoItIsAnnouncedWithOne()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();

        foreach (string heading in new[] { "prices-heading", "per-question-heading", "worst-case-heading" })
        {
            IElement table = cut.Find($"section[aria-labelledby='{heading}'] table");
            Assert.Equal(heading, table.GetAttribute("aria-labelledby"));
            Assert.NotNull(cut.Find($"#{heading}"));
        }
    }

    // --- Session cost -------------------------------------------------------------------------------

    [Fact]
    public void TheCostCard_BreaksTheOpenConversationDownByBucket_AndTotalsIt()
    {
        Guid id = Guid.NewGuid();
        TurnCost known = new(0.01m, 2m, 0.04m, 0.10m, 0.16m);
        _store.Publish(Idle(id) with
        {
            Title = TestData.Question,
            Settings = TestData.Sonnet(EffortLevel.Max),
            Cost = new ConversationCost(known, 0, 0),
        });

        IRenderedComponent<CorpusSettings> cut = RenderAt(id);
        IElement card = cut.Find(".set__card--cost");

        Assert.Equal(TestData.Question, card.QuerySelector(".set__note")!.TextContent);
        Assert.Equal("SONNET 5 · MAX", card.QuerySelector(".d1-badge")!.TextContent.Trim());
        Assert.Equal(
            ["Input $0.01", "1-hour cache write $2.00", "5-minute cache write $0.04", "Cache read $0.10", "Output $0.16"],
            card.QuerySelectorAll(".set__bucket").Select(Flat));
        Assert.Equal("TOTAL " + Formatting.Money(known.Total), Flat(card.QuerySelector(".set__total")!));
    }

    [Fact]
    public void StoppedAndFailedTurns_AreCounted_WithWhatIsNotKnownAboutThemSaidPlainly()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id) with { Cost = new ConversationCost(new TurnCost(0m, 2m, 0m, 0m, 0m), 1, 2) });

        string card = RenderAt(id).Find(".set__card--cost").TextContent;

        Assert.Contains("Excludes 1 turn of unknown cost", card, StringComparison.Ordinal);
        Assert.Contains("Output is not reported for 2 stopped or failed turns", card, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCostCard_AlsoTotalsEveryConversationOfThisRun()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        _store.PublishTotal(new ConversationCost(new TurnCost(0m, 4m, 0m, 0.2m, 0.3m), 0, 0));

        IRenderedComponent<CorpusSettings> cut = RenderAt(id);

        Assert.Contains("All conversations, this run", cut.Find(".set__card--cost").TextContent, StringComparison.Ordinal);
        Assert.Contains("$4.50", cut.Find(".set__card--cost").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoConversation_TheCostCardSaysSo_AndNoneIsStarted()
    {
        Assert.Contains(
            "No conversation is open",
            _context.Render<CorpusSettings>().Find(".set__card--cost").TextContent,
            StringComparison.Ordinal);
        Assert.Empty(_store.CreateCalls);
    }

    [Fact]
    public void TheConversationInTheAddress_IsTheOneBrokenDown_AndAnUnknownOneFallsBackToTheNewest()
    {
        Guid older = Guid.NewGuid();
        Guid newer = Guid.NewGuid();
        _store.Publish(Idle(older) with { Title = "Older" });
        _store.Publish(Idle(newer) with { Title = "Newer" });

        Assert.Equal("Older", RenderAt(older).Find(".set__card--cost .set__note").TextContent);
        Assert.Equal("Newer", RenderAt(Guid.NewGuid()).Find(".set__card--cost .set__note").TextContent);
    }

    // --- Rendering ----------------------------------------------------------------------------------

    [Fact]
    public void AStoreChangeThatChangesNothingOnScreen_DoesNotRender()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        IRenderedComponent<CorpusSettings> cut = RenderAt(id);
        int renders = cut.RenderCount;

        // As a streamed fragment does: the conversation changes, but not its cost, settings or the corpus.
        _store.Publish(Idle(id) with { InProgress = Answering(id).InProgress });
        Assert.Equal(renders, cut.RenderCount);

        ConversationCost cost = new(new TurnCost(0m, 2m, 0m, 0m, 0m), 0, 0);
        _store.Publish(Idle(id) with { Cost = cost });
        Assert.True(cut.RenderCount > renders);

        // The title is on screen too: the store sets it from the first question, which may be asked in another tab.
        // Only the title moves here, so the guard has to watch it on its own.
        renders = cut.RenderCount;
        _store.Publish(Idle(id) with { Cost = cost, Title = TestData.Question });
        Assert.True(cut.RenderCount > renders);
        Assert.Equal(TestData.Question, cut.Find(".set__card--cost .set__note").TextContent);
    }

    [Fact]
    public void Disposing_Unsubscribes()
    {
        IRenderedComponent<CorpusSettings> cut = _context.Render<CorpusSettings>();
        int store = _store.SubscriberCount;
        int corpus = _corpus.SubscriberCount;

        cut.Instance.Dispose();

        Assert.Equal(store - 1, _store.SubscriberCount);
        Assert.Equal(corpus - 1, _corpus.SubscriberCount);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private IRenderedComponent<CorpusSettings> RenderAt(Guid id)
    {
        Navigation.NavigateTo($"/settings?c={id}");
        return _context.Render<CorpusSettings>();
    }

    /// <summary>An element's text with every whitespace run collapsed, so markup indentation is not asserted on.</summary>
    private static string Flat(IElement element) =>
        string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int LevelIndex(EffortLevel effort) =>
        CacheAffecting.EffortLevels.ToList().IndexOf(effort);

    private static IReadOnlyList<IElement> Rows(IRenderedComponent<CorpusSettings> cut, string headingId) =>
        [.. cut.Find($"section[aria-labelledby='{headingId}'] tbody").QuerySelectorAll("tr")];

    private static string CacheCell(IRenderedComponent<CorpusSettings> cut, EffortLevel effort) =>
        Rows(cut, "per-question-heading")[LevelIndex(effort)].QuerySelectorAll("td")[3].TextContent;
}
