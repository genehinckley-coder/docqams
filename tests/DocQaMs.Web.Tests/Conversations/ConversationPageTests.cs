using AngleSharp.Dom;
using Bunit;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Web.Components.Conversations;
using DocQaMs.Web.Components.Pages;
using DocQaMs.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static DocQaMs.Web.Tests.TestSupport.FakeConversationStore;

namespace DocQaMs.Web.Tests.Conversations;

/// <summary>Plan items 8 and 11: the Conversation screen, against a fake store, corpus and clock.</summary>
public sealed class ConversationPageTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly FakeConversationStore _store = new();
    private readonly FakeCorpusSource _corpus = new(FakeCorpusSource.Ready(TestData.Corpus));
    private readonly FakeTimeProvider _time = new(TestData.Start);
    private readonly CacheWarmthTracker _warmth;
    private readonly CostEstimator _costs = new(Options.Create(TestData.Options()));

    public ConversationPageTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _warmth = new CacheWarmthTracker(_time);
        _context.Services.AddSingleton<IConversationStore>(_store);
        _context.Services.AddSingleton<ICorpusSource>(_corpus);
        _context.Services.AddSingleton(_warmth);
        _context.Services.AddSingleton(_costs);
        _context.Services.AddSingleton<TimeProvider>(_time);
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    public void Dispose() => _context.Dispose();

    // --- Which conversation -------------------------------------------------------------------------

    [Fact]
    public void WithNoConversations_ItStartsOne_AtTheDefaultSettings_AndShowsTheEmptyState()
    {
        IRenderedComponent<Conversation> cut = _context.Render<Conversation>();

        Assert.Equal([null], _store.CreateCalls);
        Assert.Equal("New conversation", cut.Find("h1").TextContent);
        Assert.Equal("NO QUESTIONS YET", cut.Find(".conv__heading .d1-badge").TextContent.Trim());
        Assert.Equal(
            ["What are the sump requirements for a UST in Illinois?", "How long must release detection results be kept on file?"],
            cut.FindAll("button.d1-card").Select(card => card.TextContent.Trim()));
    }

    [Fact]
    public void TheConversationInTheAddress_IsShown_AndAnUnknownOneFallsBackToTheNewest()
    {
        Guid older = Guid.NewGuid();
        Guid newer = Guid.NewGuid();
        _store.Publish(Idle(older) with { Title = "Older" });
        _store.Publish(Idle(newer) with { Title = "Newer" });

        Assert.Equal("Older", RenderAt(older).Find("h1").TextContent);
        Assert.Equal("Newer", RenderAt(Guid.NewGuid()).Find("h1").TextContent);
        Assert.Empty(_store.CreateCalls);
    }

    [Fact]
    public void TheList_ShowsEachConversationsAnswersSettingsAndCost_AndMarksTheOneOnScreen()
    {
        Guid id = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        _store.Publish(Idle(other) with { Title = "Other", Settings = TestData.Sonnet(EffortLevel.Low) });
        _store.Publish(Committed(id) with { Cost = new ConversationCost(new TurnCost(0m, 2m, 0m, 0m, 0.16m), 0, 0) });

        IRenderedComponent<Conversation> cut = RenderAt(id);

        IReadOnlyList<IElement> rows = cut.FindAll(".convlist__row");
        Assert.Equal(2, rows.Count);
        Assert.Equal("true", rows[0].GetAttribute("aria-current"));
        Assert.Equal("1 answer · OPUS 5.5 · HIGH", rows[0].QuerySelector(".convlist__meta > span")!.TextContent);
        Assert.Equal("$2.16", rows[0].QuerySelector(".convlist__cost")!.TextContent);
        Assert.Null(rows[1].GetAttribute("aria-current"));
        Assert.Equal("0 answers · SONNET 5 · LOW", rows[1].QuerySelector(".convlist__meta > span")!.TextContent);
    }

    [Fact]
    public void ClickingAConversation_PutsItInTheAddress()
    {
        Guid id = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        _store.Publish(Idle(other));
        _store.Publish(Idle(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.FindAll(".convlist__row")[1].Click();

        Assert.EndsWith($"?c={other}", Navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void NewConversation_CopiesTheSettingsOfTheOneOnScreen_AndOpensIt()
    {
        Guid id = Guid.NewGuid();
        AnswerSettings settings = TestData.Sonnet(EffortLevel.Low);
        _store.Publish(Idle(id) with { Settings = settings });
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.Find(".convlist .d1-btn").Click();

        Assert.Equal([settings], _store.CreateCalls);
        Assert.EndsWith($"?c={_store.List()[0].Id}", Navigation.Uri, StringComparison.Ordinal);
    }

    // --- Model, level and the cost preview ----------------------------------------------------------

    [Fact]
    public void AnEmptyConversation_OffersBothSelects_WithAColdPreview_FromTheEstimator()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        CostEstimate estimate = _costs.Estimate(AnswerSettings.Default, TestData.Corpus);

        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.Equal(2, cut.FindAll("select").Count);
        string preview = cut.Find(".settings__preview").TextContent;
        Assert.Contains($"Opus 5.5, High: cold, so {Formatting.Money(estimate.ColdQuestion)} for the first question (measured)", preview, StringComparison.Ordinal);
        Assert.Contains($"Up to {Formatting.Money(estimate.WorstCaseCold)} if an answer runs to its output limit.", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(CostPreview.LowLevelCaution, preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheLevel_ChangesTheConversationsSettings_AndItsPreview()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.FindAll("select")[1].Change(nameof(EffortLevel.Low));

        Assert.Equal([(id, AnswerSettings.Create(ModelIds.ClaudeOpus55, EffortLevel.Low))], _store.SettingsChanges);
        cut.WaitForAssertion(() =>
        {
            string preview = cut.Find(".settings__preview").TextContent;
            Assert.Contains("Opus 5.5, Low: cold", preview, StringComparison.Ordinal);
            Assert.Contains("(measured)", preview, StringComparison.Ordinal);
            Assert.Contains(CostPreview.LowLevelCaution, preview, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AWarmEntry_IsPreviewedAsProbablyWarm_AndGoesColdWhenItsTimeComes()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        _warmth.RecordUsage(CorpusCacheKey.For(TestData.Corpus, AnswerSettings.Default), new TokenUsage(0, TestData.Tokens, 0, 0, 0));

        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.Contains("probably warm until 12:55", cut.Find(".settings__preview").TextContent, StringComparison.Ordinal);

        // Nothing else changes when the prediction expires; the page refreshes itself.
        _time.Advance(TimeSpan.FromMinutes(56));
        cut.WaitForAssertion(() => Assert.Contains("Opus 5.5, High: cold", cut.Find(".settings__preview").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void AColdWriteInProgressElsewhere_IsWarnedOf()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        _ = _warmth.BeginRequest(CorpusCacheKey.For(TestData.Corpus, AnswerSettings.Default));

        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.StartsWith("A cold write is in progress in another conversation.", cut.Find(".settings__warning").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void LockedSettings_AreABadge_AndNewConversationWithDifferentSettingsCopiesThem()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Committed(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.Empty(cut.FindAll("select"));
        Assert.Equal("OPUS 5.5 · HIGH", cut.Find(".settings .d1-badge").TextContent.Trim());

        cut.FindAll(".settings button").Single(button => button.TextContent.Contains("different settings", StringComparison.Ordinal)).Click();

        Assert.Equal([AnswerSettings.Default], _store.CreateCalls);
        Assert.EndsWith($"?c={_store.List()[0].Id}", Navigation.Uri, StringComparison.Ordinal);
    }

    // --- Asking ---------------------------------------------------------------------------------------

    [Fact]
    public void TypingAndClickingAsk_SendsTheQuestion_AndClearsTheInput()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);
        Assert.Equal("true", AskButton(cut).GetAttribute("aria-disabled"));

        cut.Find("textarea").Input(TestData.Question);
        Assert.False(AskButton(cut).HasAttribute("aria-disabled"));
        AskButton(cut).Click();

        Assert.Equal([(id, TestData.Question)], _store.AskCalls);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find("textarea").GetAttribute("value") ?? "");
            Assert.Equal("Stop", AskButton(cut).TextContent.Trim());
        });
    }

    [Fact]
    public void Enter_SendsTheTextareasText_AndASecondEnterWhileAnsweringDoesNothing()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);
        QuestionInput input = cut.FindComponent<QuestionInput>().Instance;

        cut.InvokeAsync(() => input.SubmitFromKeyboard(TestData.Question));
        cut.InvokeAsync(() => input.SubmitFromKeyboard(TestData.Question));

        Assert.Equal([(id, TestData.Question)], _store.AskCalls);
    }

    [Fact]
    public void ARefusedQuestion_ShowsWhy_AndStaysInTheInput()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        _store.RefuseWith = "The app is shutting down.";
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.Find("textarea").Input(TestData.Question);
        AskButton(cut).Click();

        Assert.Equal("The app is shutting down.", cut.Find(".conv__error").TextContent);
        Assert.Equal(TestData.Question, cut.Find("textarea").GetAttribute("value"));
    }

    [Fact]
    public void ASuggestion_FillsTheInput_WithoutAsking()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.FindAll("button.d1-card")[0].Click();

        Assert.Equal("What are the sump requirements for a UST in Illinois?", cut.Find("textarea").GetAttribute("value"));
        Assert.Empty(_store.AskCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhileTheCorpusIsNotReady_AskIsUnavailable_AndSaysWhy(bool failed)
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        _corpus.Publish(failed ? new CorpusState.Failed("No network.", TestData.Start) : new CorpusState.Loading());
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.Find("textarea").Input(TestData.Question);

        IElement ask = AskButton(cut);
        Assert.Equal("true", ask.GetAttribute("aria-disabled"));
        string reason = cut.Find("#" + ask.GetAttribute("aria-describedby")).TextContent;
        Assert.Contains(failed ? "could not be loaded" : "still loading", reason, StringComparison.Ordinal);
        Assert.Equal(failed ? "CORPUS NOT LOADED" : "CORPUS LOADING", cut.Find(".conv__notice .d1-badge").TextContent.Trim());
        Assert.Equal("The cost preview appears once the corpus has loaded.", cut.Find(".settings__preview").TextContent.Trim());

        ask.Click();
        Assert.Empty(_store.AskCalls);
    }

    [Fact]
    public void AFailedLoad_OffersARetry()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        _corpus.Publish(new CorpusState.Failed("No network.", TestData.Start));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.FindAll(".conv__notice button").Single(button => button.TextContent.Trim() == "Retry").Click();

        Assert.Equal(1, _corpus.Reloads);

        // The load's outcome arrives as a state change, and Ask becomes available.
        _corpus.Publish(FakeCorpusSource.Ready(TestData.Corpus));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".conv__notice")));
    }

    [Fact]
    public void AConversationWhoseCorpusAReloadReplaced_IsReadOnly_WithANotice()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Committed(id));
        _corpus.Publish(FakeCorpusSource.Ready(TestData.CreateCorpus("fingerprint-2")));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        cut.Find("textarea").Input("A follow-up?");

        Assert.Equal("CORPUS RELOADED", cut.Find(".conv__notice .d1-badge").TextContent.Trim());
        Assert.Equal("true", AskButton(cut).GetAttribute("aria-disabled"));
        Assert.Single(cut.FindAll("article.qa"));
    }

    // --- The returned question ----------------------------------------------------------------------

    [Fact]
    public void AFreshRenderAfterAStop_ShowsTheReturnedQuestionInTheInput()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Finished(id, TurnOutcome.Stopped) with { Answers = [TestData.Stopped()], ReturnedQuestion = TestData.Question });

        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.Equal(TestData.Question, cut.Find("textarea").GetAttribute("value"));
        Assert.Equal(1, _store.Takes);
        Assert.Null(_store.Get(id)!.ReturnedQuestion);
        Assert.Contains("Stopped: not checked, not added to the conversation.", cut.Find(".qa__notice").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void APrerender_LeavesTheReturnedQuestionForTheLiveCircuit()
    {
        _context.SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        Guid id = Guid.NewGuid();
        _store.Publish(Finished(id, TurnOutcome.Failed) with { ReturnedQuestion = TestData.Question });

        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.Equal("", cut.Find("textarea").GetAttribute("value") ?? "");
        Assert.Equal(TestData.Question, _store.Get(id)!.ReturnedQuestion);
    }

    [Fact]
    public void AQuestionReturnedWhileTheTabIsOpen_ReachesTheInput()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Answering(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        _store.Publish(Finished(id, TurnOutcome.Failed) with { Answers = [TestData.Stopped() with { Outcome = TurnOutcome.Failed }], ReturnedQuestion = TestData.Question });

        cut.WaitForAssertion(() => Assert.Equal(TestData.Question, cut.Find("textarea").GetAttribute("value")));
    }

    // --- Streaming ------------------------------------------------------------------------------------

    [Fact]
    public void AnAnswerInFlight_ShowsTheAssemblingIndicator_TheThinkingSoFar_AndStop()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Streaming(id, thinking: "Checking Section 175.410."));

        IRenderedComponent<Conversation> cut = RenderAt(id);

        IElement live = cut.Find("article.qa--live");
        Assert.Equal("true", live.GetAttribute("aria-busy"));
        Assert.Contains("Assembling answer from documents", live.QuerySelector(".qa__assembling")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("Reasoning summary so far", live.QuerySelector("details.qa__reasoning > summary")!.TextContent);
        Assert.Equal("Checking Section 175.410.", live.QuerySelector("details.qa__reasoning .qa__reasoning-text")!.TextContent);
        Assert.Equal("Stop", AskButton(cut).TextContent.Trim());
    }

    [Fact]
    public void AnAnswerInFlight_WithNoThinkingYet_StillShowsTheReasoningArea_AndSaysNoneHasArrived()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Answering(id));

        IElement live = RenderAt(id).Find("article.qa--live");

        IElement details = live.QuerySelector("details.qa__reasoning")!;
        Assert.False(details.HasAttribute("open"));
        Assert.Equal("No reasoning has arrived yet.", details.QuerySelector(".qa__reasoning-note")!.TextContent);
    }

    [Fact]
    public void AStreamingStructuredAnswer_ShowsItsSummary_AndItsDetailCollapsed_AsBeingWritten()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Answering(id) with
        {
            InProgress = Answering(id).InProgress! with { Segments = [new AnswerSegment("Summary\nYes.\n\nDetail\nUnder Section", [])] },
        });

        IElement live = RenderAt(id).Find("article.qa--live");

        Assert.Equal("Yes.", live.QuerySelector(":scope > .answer-text")!.TextContent.Trim());
        IElement detail = live.QuerySelector("details.qa__detail")!;
        Assert.False(detail.HasAttribute("open"));
        Assert.Equal("Detail · writing…", detail.QuerySelector("summary")!.TextContent);
        Assert.Equal("Under Section", detail.QuerySelector(".answer-text")!.TextContent.Trim());
        Assert.Null(live.QuerySelector(".qa__assembling"));
    }

    [Fact]
    public void StreamedFragments_RenderAtMostEvery100Ms_ButTheEndRendersAtOnce()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Idle(id));
        IRenderedComponent<Conversation> cut = RenderAt(id);

        // The answer starting renders at once.
        _store.Publish(Streaming(id, thinking: ""));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article.qa--live")));

        // A fragment waits for the interval.
        _store.Publish(Streaming(id, thinking: "First thoughts."));
        Assert.Empty(cut.FindAll(".qa__reasoning-text"));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        cut.WaitForAssertion(() => Assert.Equal("First thoughts.", cut.Find(".qa__reasoning-text").TextContent));

        // More fragments, then the end, with no time passing: the end is never held back.
        _store.Publish(Streaming(id, thinking: "First thoughts. More."));
        _store.Publish(Committed(id) with { LastOutcome = TurnOutcome.Completed });
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("article.qa--live"));
            Assert.Single(cut.FindAll("article.qa"));
            Assert.Equal("Ask", AskButton(cut).TextContent.Trim());
        });
    }

    // --- Answers ----------------------------------------------------------------------------------------

    [Fact]
    public void TheDisclaimer_IsOnTheFirstAnswerWithSources_Only()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Committed(id) with { Answers = [TestData.Completed(), TestData.Completed(question: "And after?")] });

        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.Equal(2, cut.FindAll("article.qa").Count);
        IElement disclaimer = Assert.Single(cut.FindAll(".qa__disclaimer"));
        Assert.Same(cut.FindAll("article.qa")[0], disclaimer.Closest("article"));
    }

    [Fact]
    public void AMarkersLink_KeepsTheConversationInTheAddress()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Committed(id));

        IRenderedComponent<Conversation> cut = RenderAt(id);

        // Without the query string, following the link would open the newest conversation instead of this one.
        Assert.Equal($"http://localhost/?c={id}#answer-0-source-1", cut.Find("a.answer-text__marker").GetAttribute("href"));
    }

    [Fact]
    public void AnAddressNamingASource_OpensThatAnswersSources_Only()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Committed(id) with { Answers = [TestData.Completed(), TestData.Completed(question: "And after?")] });

        // As the Documents screen links back.
        Navigation.NavigateTo($"/?c={id}#answer-1-source-1");
        IRenderedComponent<Conversation> cut = _context.Render<Conversation>();

        IReadOnlyList<IElement> sources = cut.FindAll(".qa__sources details");
        Assert.False(sources[0].HasAttribute("open"));
        Assert.True(sources[1].HasAttribute("open"));
    }

    [Theory]
    [InlineData(CacheStatus.ColdWrite, "COLD CACHE WRITE")]
    [InlineData(CacheStatus.Read, "CACHE READ")]
    [InlineData(CacheStatus.Miss, "CACHE MISS")]
    [InlineData(CacheStatus.NotCached, "NOT CACHED")]
    public void TheHeaderBadge_ShowsTheLatestCacheReport(CacheStatus status, string text)
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Committed(id) with { Answers = [TestData.Completed(), TestData.Completed(cache: new CacheReport(status))] });

        Assert.Equal(text, RenderAt(id).Find(".conv__heading .d1-badge").TextContent.Trim());
    }

    [Fact]
    public void TheTitleAndAnswers_AreEncoded_NeverMarkup()
    {
        const string payload = "<script>alert(1)</script>";
        Guid id = Guid.NewGuid();
        _store.Publish(Committed(id) with { Title = payload, Answers = [TestData.Completed(question: payload, text: payload)] });

        IRenderedComponent<Conversation> cut = RenderAt(id);

        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("&lt;script&gt;", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Disposing_Unsubscribes()
    {
        IRenderedComponent<Conversation> cut = _context.Render<Conversation>();
        int subscribed = _store.SubscriberCount;

        cut.Instance.Dispose();

        // The Ask/Stop button and the page each held one subscription; the page's is gone.
        Assert.Equal(subscribed - 1, _store.SubscriberCount);
    }

    private NavigationManager Navigation => _context.Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Conversation> RenderAt(Guid id)
    {
        Navigation.NavigateTo($"/?c={id}");
        return _context.Render<Conversation>();
    }

    private static IElement AskButton(IRenderedComponent<Conversation> cut) => cut.Find(".conv__composer-row > button");

    private static ConversationSnapshot Committed(Guid id) => Idle(id) with
    {
        Title = TestData.Question,
        SettingsLocked = true,
        Corpus = TestData.Corpus,
        Messages = [new() { Role = Anthropic.Models.Messages.Role.User, Content = "q" }, new() { Role = Anthropic.Models.Messages.Role.Assistant, Content = "a" }],
        Answers = [TestData.Completed()],
        LastOutcome = TurnOutcome.Completed,
    };

    private static ConversationSnapshot Streaming(Guid id, string thinking) => Answering(id) with
    {
        InProgress = Answering(id).InProgress! with { Thinking = thinking },
    };
}
