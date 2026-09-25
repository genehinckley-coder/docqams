using AngleSharp.Dom;
using Bunit;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using DocQaMs.Core.Text;
using DocQaMs.Web.Components.Pages;
using DocQaMs.Web.Components.Viewer;
using DocQaMs.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;
using static DocQaMs.Web.Tests.TestSupport.FakeConversationStore;

namespace DocQaMs.Web.Tests.Viewer;

/// <summary>Plan item 9: the Documents screen, against a fake store and corpus.</summary>
public sealed class DocumentsPageTests : IDisposable
{
    private const string ModulePath = "./Components/Pages/Documents.razor.js";

    private const string LabelA = "41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks";
    private const string LabelB = "41 Ill. Adm. Code 177: Deposit of Petroleum";

    // Paragraphs 0 and 1 are the contents; 2 and 4 are the body headings the tags point at.
    private const string PartA =
        "Section 175.100 Definitions\n\nSection 175.410 Sumps\n\nSection 175.100 Definitions\n\nTerms mean what they say."
        + "\n\nSection 175.410 Sumps\n\nA sump must have a minimum capacity of 5 gallons.\n\n<script>alert(1)</script>";

    private const string PartB = "Section 177.100 Definitions\n\nDeposit means placing a substance in a UST.";

    private const string SumpQuote = "minimum capacity of 5 gallons";

    private readonly BunitContext _context = new();
    private readonly FakeConversationStore _store = new();
    private readonly FakeCorpusSource _corpusSource;
    private readonly BunitJSModuleInterop _module;

    public DocumentsPageTests()
    {
        _corpusSource = new FakeCorpusSource(FakeCorpusSource.Ready(NewCorpus()));
        _context.Services.AddSingleton<IConversationStore>(_store);
        _context.Services.AddSingleton<ICorpusSource>(_corpusSource);
        _context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        _module = _context.JSInterop.SetupModule(ModulePath);
        _module.SetupVoid("reveal", _ => true);
    }

    public void Dispose() => _context.Dispose();

    private NavigationManager Navigation => _context.Services.GetRequiredService<NavigationManager>();

    // --- The list -----------------------------------------------------------------------------------

    [Fact]
    public void TheList_ShowsEachDocumentsCounts_AndMarksTheOnesTheConversationCites()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        _store.Publish(Conversation(id, corpus, Answer(corpus, Check(SumpQuote))));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}");

        IReadOnlyList<IElement> rows = cut.FindAll(".docs__row");
        Assert.Equal(2, rows.Count);
        Assert.Equal("true", rows[0].GetAttribute("aria-current"));
        Assert.Equal(LabelA, rows[0].QuerySelector(".docs__row-label")!.TextContent);
        Assert.Equal($"{PartA.Length:N0} characters · about 1,000 tokens", rows[0].QuerySelector(".docs__row-meta")!.TextContent);
        Assert.Equal("CITED", rows[0].QuerySelector(".d1-badge")!.TextContent.Trim());
        Assert.Null(rows[1].GetAttribute("aria-current"));
        Assert.Equal($"{PartB.Length:N0} characters · tokens not counted", rows[1].QuerySelector(".docs__row-meta")!.TextContent);
        Assert.Null(rows[1].QuerySelector(".d1-badge"));
        Assert.Equal("Corpus · 2 documents", cut.Find(".docs__eyebrow").TextContent);
    }

    [Fact]
    public void ACitationCheckedAgainstAReplacedCorpus_MarksNothing()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot older = NewCorpus("fingerprint-older");
        CorpusSnapshot current = NewCorpus();
        _store.Publish(Conversation(id, current, Answer(older, Check(SumpQuote))));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}");

        Assert.Empty(cut.FindAll(".docs__row .d1-badge"));
    }

    [Fact]
    public void WithNoConversationInTheAddress_TheNewestIsUsed()
    {
        CorpusSnapshot corpus = NewCorpus();
        _store.Publish(Conversation(Guid.NewGuid(), corpus));
        _store.Publish(Conversation(Guid.NewGuid(), corpus, Answer(corpus, Check("Deposit means placing", PartB, document: 1))));

        IRenderedComponent<Documents> cut = RenderAt();

        Assert.Equal("CITED", cut.FindAll(".docs__row")[1].QuerySelector(".d1-badge")!.TextContent.Trim());
    }

    [Fact]
    public void ChoosingADocument_PutsItInTheAddress_AndDropsTheSource()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        _store.Publish(Conversation(id, corpus, Answer(corpus, Check(SumpQuote))));
        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");

        cut.FindAll(".docs__row")[1].Click();

        Assert.Contains($"c={id}", Navigation.Uri, StringComparison.Ordinal);
        Assert.Contains("doc=1", Navigation.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("answer=", Navigation.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("source=", Navigation.Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&doc=1", LabelB)]
    [InlineData("&doc=9", LabelA)]
    [InlineData("&doc=-1", LabelA)]
    [InlineData("", LabelA)]
    public void TheDocumentInTheAddress_IsShown_AndAnImpossibleOneFallsBackToTheFirst(string query, string label)
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Conversation(id, NewCorpus()));

        Assert.Equal(label, RenderAt($"?c={id}{query}").Find("h1").TextContent);
    }

    // --- The document -------------------------------------------------------------------------------

    [Fact]
    public void TheWholeExtractedText_IsShown_AsParagraphsAndAsText()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Conversation(id, NewCorpus()));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}");

        Assert.Equal(PartA.Split("\n\n"), cut.FindAll("p.docpara").Select(paragraph => paragraph.TextContent));
        Assert.Empty(cut.FindAll("mark"));
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("&lt;script&gt;", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("ADMINISTRATIVE CODE 41 175.htm", cut.Find(".docs__heading .docs__eyebrow").TextContent);
    }

    [Fact]
    public void TheSectionTags_AreTheBodyHeadings_AndNameThemInFull()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Conversation(id, NewCorpus()));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}");

        IReadOnlyList<IElement> tags = cut.FindAll(".docs__tags button");
        Assert.Equal(["175.100", "175.410"], tags.Select(tag => tag.TextContent.Trim()));
        Assert.Equal(
            ["Section 175.100 Definitions", "Section 175.410 Sumps"],
            tags.Select(tag => tag.GetAttribute("aria-label")));
        Assert.Equal("2 sections", cut.Find(".docs__sections > summary").TextContent);
    }

    [Fact]
    public void ClickingASectionTag_ScrollsToItsBodyHeading()
    {
        Guid id = Guid.NewGuid();
        _store.Publish(Conversation(id, NewCorpus()));
        IRenderedComponent<Documents> cut = RenderAt($"?c={id}");

        cut.FindAll(".docs__tags button")[1].Click();

        Assert.Equal(["paragraph-4", "start"], Assert.Single(_module.Invocations["reveal"]).Arguments);
    }

    // --- The source clicked through to --------------------------------------------------------------

    [Fact]
    public void ASource_IsHighlightedInItsParagraph_ScrolledTo_AndDescribed()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        _store.Publish(Conversation(id, corpus, Answer(corpus, Check(SumpQuote))));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");

        IElement mark = cut.Find("mark");
        Assert.Equal(SumpQuote, mark.TextContent);
        Assert.Equal("paragraph-5", mark.ParentElement!.Id);
        Assert.Equal("A sump must have a minimum capacity of 5 gallons.", mark.ParentElement.TextContent);
        Assert.Equal(["paragraph-5", "center"], Assert.Single(_module.Invocations["reveal"]).Arguments);

        int start = PartA.IndexOf(SumpQuote, StringComparison.Ordinal);
        Assert.Equal(
            $"Source 1 of answer 1 · characters {start:N0}–{start + SumpQuote.Length:N0}",
            cut.Find(".docs__citation-label").TextContent);
        Assert.Equal("matched", cut.Find(".docs__citation .d1-badge").TextContent.Trim());
        Assert.Equal($"“{SumpQuote}”", cut.Find(".docs__citation-quote").TextContent);
        Assert.Equal($"{Navigation.BaseUri}?c={id}#answer-0-source-1", cut.Find(".docs__back").GetAttribute("href"));
    }

    [Fact]
    public void ASpanAcrossParagraphs_HighlightsEachPart_AndNotTheBlankLineBetween()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        _store.Publish(Conversation(id, corpus, Answer(corpus, Check("Sumps\n\nA sump"))));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");

        IReadOnlyList<IElement> marks = cut.FindAll("mark");
        Assert.Equal(["Sumps", "A sump"], marks.Select(mark => mark.TextContent));
        Assert.Equal(["paragraph-4", "paragraph-5"], marks.Select(mark => mark.ParentElement!.Id));
        Assert.Equal(["paragraph-4", "center"], Assert.Single(_module.Invocations["reveal"]).Arguments);
    }

    [Fact]
    public void AMismatchedSource_IsStillHighlighted_AndSaysTheTextIsNotTheQuote()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        QuoteCheck check = Check(SumpQuote, status: MatchStatus.Mismatch, detail: "The quote departs from the span at character 4.");
        _store.Publish(Conversation(id, corpus, Answer(corpus, check)));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");

        Assert.Equal("mismatch", cut.Find(".docs__citation .d1-badge").TextContent.Trim());
        Assert.Equal(SumpQuote, cut.Find("mark").TextContent);
        Assert.Equal(
            ["The quote departs from the span at character 4.", "The highlight shows the text at the cited span, which is not the quote above."],
            cut.FindAll(".docs__citation-detail").Select(detail => detail.TextContent));
    }

    [Fact]
    public void ASourceWhoseSpanIsOutOfRange_SaysThereIsNothingToHighlight()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        var check = new QuoteCheck(
            new AnswerCitation(0, 5_000, 5_100, SumpQuote, LabelA), "char_location", SumpQuote, MatchStatus.OutOfRange, "Outside the text.");
        _store.Publish(Conversation(id, corpus, Answer(corpus, check)));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");

        Assert.Empty(cut.FindAll("mark"));
        Assert.Empty(_module.Invocations["reveal"]);
        Assert.Contains(
            "Its span covers no text that can be highlighted.",
            cut.FindAll(".docs__citation-detail").Select(detail => detail.TextContent));
    }

    [Theory]
    [InlineData("&answer=7&source=1")]
    [InlineData("&answer=0&source=9")]
    [InlineData("&answer=0")]
    public void ASourceThatCannotBeFound_SaysSo_AndHighlightsNothing(string query)
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        _store.Publish(Conversation(id, corpus, Answer(corpus, Check(SumpQuote))));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}{query}");

        Assert.Empty(cut.FindAll("mark"));
        Assert.Empty(_module.Invocations["reveal"]);
        Assert.Empty(cut.FindAll(".docs__citation"));
        Assert.Contains("SOURCE NOT FOUND", cut.Find(".docs__notice").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceFromBeforeAReload_IsShownInTheTextItWasCheckedAgainst()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot answered = NewCorpus("fingerprint-older");
        CorpusSnapshot current = NewCorpus("fingerprint-current", PartA.Replace("5 gallons", "9 gallons", StringComparison.Ordinal));
        _corpusSource.Publish(FakeCorpusSource.Ready(current));
        _store.Publish(Conversation(id, answered, Answer(answered, Check(SumpQuote))));

        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");

        Assert.Equal("A sump must have a minimum capacity of 5 gallons.", cut.Find("mark").ParentElement!.TextContent);
        Assert.Contains("CORPUS RELOADED", cut.Find(".docs__notice").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceThatArrivesAfterTheScreenOpens_IsScrolledToWhenItDoes_AndOnlyOnce()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        _store.Publish(Conversation(id, corpus));
        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");
        Assert.Empty(_module.Invocations["reveal"]);

        _store.Publish(Conversation(id, corpus, Answer(corpus, Check(SumpQuote))));

        cut.WaitForAssertion(() => Assert.Equal(["paragraph-5", "center"], Assert.Single(_module.Invocations["reveal"]).Arguments));

        _store.Publish(Conversation(
            id, corpus, Answer(corpus, Check(SumpQuote)), Answer(corpus, Check("Deposit means placing", PartB, document: 1))));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".docs__row .d1-badge").Count));
        Assert.Single(_module.Invocations["reveal"]);
    }

    // --- Rendering cost -----------------------------------------------------------------------------

    [Fact]
    public void ChangingTheHighlight_RendersOnlyTheParagraphsItTouches()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = Part175();
        CorpusDocument document = corpus.Documents[0];
        Assert.True(document.CodePoints.IsIdentity, "Part 175 has no surrogate pair, so its offsets are also UTF-16 offsets.");
        _store.Publish(Conversation(id, corpus, Answer(corpus, WholeParagraph(document, 300)), Answer(corpus, WholeParagraph(document, 700))));
        IRenderedComponent<Documents> cut = RenderAt($"?c={id}&answer=0&source=1");

        IReadOnlyList<IRenderedComponent<DocumentParagraph>> paragraphs = cut.FindComponents<DocumentParagraph>();
        Assert.Equal(document.Paragraphs.Count, paragraphs.Count);
        int[] before = [.. paragraphs.Select(paragraph => paragraph.RenderCount)];

        Navigation.NavigateTo($"/documents?c={id}&answer=1&source=1");

        cut.WaitForAssertion(() => Assert.Equal("paragraph-700", cut.Find("mark").ParentElement!.Id));
        int[] after = [.. cut.FindComponents<DocumentParagraph>().Select(paragraph => paragraph.RenderCount)];
        int[] rendered = [.. Enumerable.Range(0, after.Length).Where(i => after[i] > before[i])];
        Assert.Equal([300, 700], rendered);
    }

    [Fact]
    public void AStreamedFragment_DoesNotRenderTheScreen_ButANewAnswerDoes()
    {
        Guid id = Guid.NewGuid();
        CorpusSnapshot corpus = NewCorpus();
        ConversationSnapshot conversation = Conversation(id, corpus, Answer(corpus, Check(SumpQuote)));
        _store.Publish(conversation);
        IRenderedComponent<Documents> cut = RenderAt($"?c={id}");
        int renders = cut.RenderCount;

        _store.Publish(conversation with { InProgress = Answering(id).InProgress });
        _store.Publish(conversation with
        {
            Answers = [.. conversation.Answers, Answer(corpus, Check("Deposit means placing", PartB, document: 1))],
        });

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".docs__row .d1-badge").Count));
        Assert.Equal(renders + 1, cut.RenderCount);
    }

    // --- Without a corpus ---------------------------------------------------------------------------

    [Fact]
    public void WhileTheCorpusLoads_TheScreenSaysSo()
    {
        _corpusSource.Publish(new CorpusState.Loading());

        IRenderedComponent<Documents> cut = RenderAt();

        Assert.Equal("Documents", cut.Find("h1").TextContent);
        Assert.Contains("CORPUS LOADING", cut.Find(".docs__notice").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".docs__row"));
    }

    [Fact]
    public void AFailedLoad_ShowsItsReason_AndRetries()
    {
        _corpusSource.Publish(new CorpusState.Failed("No key file was found.", DateTimeOffset.UnixEpoch));
        IRenderedComponent<Documents> cut = RenderAt();
        Assert.Contains("No key file was found.", cut.Find(".docs__notice").TextContent, StringComparison.Ordinal);

        cut.Find(".docs__notice button").Click();

        Assert.Equal(1, _corpusSource.Reloads);
    }

    [Fact]
    public void WhenTheCorpusBecomesReady_TheDocumentsAppear()
    {
        _corpusSource.Publish(new CorpusState.Loading());
        IRenderedComponent<Documents> cut = RenderAt();

        _corpusSource.Publish(FakeCorpusSource.Ready(NewCorpus()));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".docs__row").Count));
    }

    [Fact]
    public async Task Disposing_Unsubscribes()
    {
        IRenderedComponent<Documents> cut = RenderAt();
        Assert.Equal(1, _store.SubscriberCount);
        Assert.Equal(1, _corpusSource.SubscriberCount);

        await cut.Instance.DisposeAsync();

        Assert.Equal(0, _store.SubscriberCount);
        Assert.Equal(0, _corpusSource.SubscriberCount);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private IRenderedComponent<Documents> RenderAt(string query = "")
    {
        Navigation.NavigateTo("/documents" + query);
        return _context.Render<Documents>();
    }

    private static CorpusSnapshot NewCorpus(string fingerprint = "fingerprint-1", string? partA = null) => new(
        [
            new CorpusDocument(
                "ADMINISTRATIVE CODE 41 175.htm",
                new RequestDocument(LabelA, "Source file: ADMINISTRATIVE CODE 41 175.htm", partA ?? PartA),
                1_000),
            new CorpusDocument(
                "ADMINISTRATIVE CODE 41 177.htm",
                new RequestDocument(LabelB, "Source file: ADMINISTRATIVE CODE 41 177.htm", PartB),
                null),
        ],
        fingerprint,
        CacheAffecting.SelectableModels.ToDictionary(model => model.Id, _ => TestData.Tokens));

    private static CorpusSnapshot Part175() => new(
        [
            new CorpusDocument(
                "ADMINISTRATIVE CODE 41 175.htm",
                new RequestDocument(LabelA, "Source file: ADMINISTRATIVE CODE 41 175.htm", File.ReadAllText(RepoPaths.Combine("tests", "golden", "175.txt"))),
                102_061),
        ],
        "fingerprint-175",
        CacheAffecting.SelectableModels.ToDictionary(model => model.Id, _ => TestData.Tokens));

    private static ConversationSnapshot Conversation(Guid id, CorpusSnapshot corpus, params AnswerRecord[] answers) =>
        Idle(id) with { Corpus = corpus, Answers = answers };

    private static AnswerRecord Answer(CorpusSnapshot corpus, params QuoteCheck[] checks) =>
        TestData.Completed(quotes: TestData.Report(checks)) with { Corpus = corpus };

    private static QuoteCheck Check(
        string quote, string text = PartA, int document = 0, MatchStatus status = MatchStatus.Matched, string? detail = null)
    {
        int start = text.IndexOf(quote, StringComparison.Ordinal);
        Assert.InRange(start, 0, text.Length);
        return new QuoteCheck(new AnswerCitation(document, start, start + quote.Length, quote, LabelA), "char_location", quote, status, detail);
    }

    // A citation covering one whole paragraph of the real Part 175, whose offsets are also its code points.
    private static QuoteCheck WholeParagraph(CorpusDocument document, int paragraph)
    {
        TextRange range = document.Paragraphs[paragraph];
        string quote = document.Text[range.Start..range.End];
        return new QuoteCheck(new AnswerCitation(0, range.Start, range.End, quote, LabelA), "char_location", quote, MatchStatus.Matched, null);
    }
}
