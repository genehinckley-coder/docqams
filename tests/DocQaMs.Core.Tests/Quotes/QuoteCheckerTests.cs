using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using DocQaMs.Core.Tests.TestSupport;
using DocQaMs.Core.Text;
using Microsoft.Extensions.Options;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Core.Tests.Quotes;

/// <summary>
/// Plan item 5: the quote checker, ported from the prototype's <c>test_quotes.py</c>, plus the code-point
/// cases of plan item 11. All offline.
/// </summary>
public sealed class QuoteCheckerTests
{
    private const string Section =
        "Section 175.410 Tank Sumps\n\n" +
        "a) Sumps shall be liquid tight.\n\n" +
        "b) Sumps shall be tested annually.\n\n";

    private const string Records = "Section 176.430 Records\n\nb) Records shall be kept for five years.\n\n";

    private const string Tight = "a) Sumps shall be liquid tight.";

    private const string Tested = "b) Sumps shall be tested annually.";

    private const string Title = "41 Ill. Adm. Code 175";

    private static readonly QuoteChecker Checker = new(Options.Create(TestSettings.ValidOptions()));

    private static readonly int MinimumLength = TestSettings.ValidOptions().MinCitationLength;

    private static readonly CorpusSnapshot Documents = CorpusOf(Section, Records);

    // --- One citation: match ----------------------------------------------------

    [Fact]
    public void AnExactQuote_Matches()
    {
        QuoteCheck check = Checker.Check(CitePhrase(Tight), Documents);

        Assert.Equal(MatchStatus.Matched, check.Status);
        Assert.True(check.IsMatched);
        Assert.Null(check.Detail);
    }

    [Fact]
    public void AQuoteInALaterDocument_Matches() =>
        Assert.Equal(MatchStatus.Matched, Checker.Check(CitePhrase("b) Records shall be kept", document: 1), Documents).Status);

    [Fact]
    public void ASpanAcrossLineBreaks_Matches() =>
        Assert.Equal(MatchStatus.Matched, Checker.Check(CitePhrase("tight.\n\nb) Sumps"), Documents).Status);

    [Fact]
    public void ASpanThatAbsorbsTrailingSeparators_Matches()
    {
        AnswerCitation citation = CitePhrase(Tight, extra: 2);

        Assert.EndsWith("\n\n", citation.CitedText, StringComparison.Ordinal);
        Assert.Equal(MatchStatus.Matched, Checker.Check(citation, Documents).Status);
    }

    [Fact]
    public void ASpanAtTheMinimumLength_Matches_AndOneShortIsDegenerate()
    {
        long start = Section.IndexOf("a)", StringComparison.Ordinal);
        long end = start;
        while (Visible(Section[(int)start..(int)end]) < MinimumLength)
        {
            end++;
        }

        Assert.Equal(MatchStatus.Matched, Checker.Check(Cite(start, end), Documents).Status);
        Assert.Equal(MatchStatus.Degenerate, Checker.Check(Cite(start, end - 1), Documents).Status);
    }

    // --- One citation: mismatch -------------------------------------------------

    [Fact]
    public void AnOffByOneOffset_IsAMismatch_ThatNamesTheRealPosition()
    {
        long start = Section.IndexOf(Tight, StringComparison.Ordinal);

        QuoteCheck check = Checker.Check(Cite(start + 1, start + Tight.Length + 1, Tight), Documents);

        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains($"appear verbatim at character {start} (-1 from the cited start)", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteNotInTheDocument_IsAMismatch()
    {
        long start = Section.IndexOf(Tight, StringComparison.Ordinal);

        QuoteCheck check = Checker.Check(Cite(start, start + Tight.Length, "a) Sumps shall be liquid proof."), Documents);

        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains("at character 25 of the span", check.Detail, StringComparison.Ordinal);
        Assert.Contains("appears nowhere verbatim", check.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("the span is", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteOfAnotherLength_ReportsBothLengths()
    {
        long start = Section.IndexOf(Tight, StringComparison.Ordinal);

        QuoteCheck check = Checker.Check(Cite(start, start + Tight.Length, Tight + " Always."), Documents);

        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains($"at character {Tight.Length} of the span", check.Detail, StringComparison.Ordinal);
        Assert.Contains($"the span is {Tight.Length} characters and the quote {Tight.Length + 8}", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMismatchDetail_FormatsLargeNumbersWithThousandsSeparators()
    {
        // Text after the quote keeps the shifted span in range, so it is a mismatch rather than out of range.
        string text = new string('x', 2_000) + " " + Tight + " More text follows.";
        CorpusSnapshot corpus = CorpusOf(text);

        QuoteCheck check = Checker.Check(new AnswerCitation(0, 2_002, 2_002 + Tight.Length, Tight, Title), corpus);

        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains("appear verbatim at character 2,001 (-1 from the cited start)", check.Detail, StringComparison.Ordinal);
    }

    // --- One citation: out of range ---------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(-1)]
    public void ADocumentIndexOutsideTheCorpus_IsOutOfRange(long documentIndex)
    {
        QuoteCheck check = Checker.Check(Cite(0, 10, "Section 17") with { DocumentIndex = documentIndex }, Documents);

        Assert.Equal(MatchStatus.OutOfRange, check.Status);
        Assert.Contains($"document {documentIndex} does not exist; the corpus has 2", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpanPastTheEnd_IsOutOfRange_EvenWhenClippingWouldMatch()
    {
        // The trap: Python's slicing clipped silently, so the clipped span equals the quote.
        int start = Section.Length - 20;

        QuoteCheck check = Checker.Check(Cite(start, Section.Length + 5, Section[start..]), Documents);

        Assert.Equal(MatchStatus.OutOfRange, check.Status);
        Assert.Contains($"which has {Section.Length}", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeStart_IsOutOfRange() =>
        Assert.Equal(MatchStatus.OutOfRange, Checker.Check(Cite(-5, 10, "whatever"), Documents).Status);

    [Fact]
    public void ASpanThatEndsBeforeItStarts_IsOutOfRange()
    {
        QuoteCheck check = Checker.Check(Cite(20, 10, ""), Documents);

        Assert.Equal(MatchStatus.OutOfRange, check.Status);
        Assert.Contains("ends", check.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(long.MaxValue, 0L, 10L)]
    [InlineData(long.MinValue, 0L, 10L)]
    [InlineData(0L, 0L, long.MaxValue)]
    [InlineData(0L, long.MinValue, 10L)]
    [InlineData(0L, long.MinValue, long.MaxValue)]
    [InlineData(0L, long.MaxValue, long.MinValue)]
    [InlineData(0L, 2_147_483_648L, 2_147_483_649L)]
    public void OffsetsBeyondAnInt_AreOutOfRange_AndNeverThrow(long documentIndex, long start, long end)
    {
        QuoteCheck check = Checker.Check(new AnswerCitation(documentIndex, start, end, "whatever quote", Title), Documents);

        Assert.Equal(MatchStatus.OutOfRange, check.Status);
        Assert.NotNull(check.Detail);
    }

    // --- One citation: degenerate -----------------------------------------------

    [Fact]
    public void AnEmptySpanAgainstAnEmptyQuote_IsDegenerate()
    {
        QuoteCheck check = Checker.Check(Cite(10, 10, ""), Documents);

        Assert.Equal(MatchStatus.Degenerate, check.Status);
        Assert.Equal("the span is empty", check.Detail);
    }

    [Fact]
    public void AWhitespaceOnlySpan_IsDegenerate()
    {
        string spaces = new(' ', 20);
        CorpusSnapshot corpus = CorpusOf("x" + spaces + "y");

        QuoteCheck check = Checker.Check(new AnswerCitation(0, 1, 21, spaces, Title), corpus);

        Assert.Equal(MatchStatus.Degenerate, check.Status);
        Assert.Equal($"the span has 0 non-whitespace characters, under the minimum citation length ({MinimumLength})", check.Detail);
    }

    // --- One citation: not checkable --------------------------------------------

    [Fact]
    public void ACitationWithoutCharacterOffsets_IsNotCheckable_NotThrown()
    {
        TextCitation page = RawCitation(
            """{"type":"page_location","cited_text":"Sumps shall be liquid tight.","document_index":0,"document_title":null,"start_page_number":1,"end_page_number":2}""");

        QuoteReport report = Report([], Block("Claim.", page));

        QuoteCheck check = Assert.Single(report.Checks);
        Assert.Equal(MatchStatus.NotCheckable, check.Status);
        Assert.False(check.IsMatched);
        Assert.Null(check.Citation);
        Assert.Equal("page_location", check.CitationType);
        Assert.Equal("Sumps shall be liquid tight.", check.CitedText);
        Assert.Contains("a \"page_location\" citation carries no character offsets", check.Detail, StringComparison.Ordinal);
        Assert.Equal([(1, check)], report.Failures);
    }

    [Fact]
    public void ACharLocationCitationMissingAnOffset_IsNotCheckable()
    {
        TextCitation partial = RawCitation(
            """{"type":"char_location","cited_text":"Sumps shall be liquid tight.","document_index":0,"document_title":"T","start_char_index":3}""");

        QuoteCheck check = Assert.Single(Report([], Block("Claim.", partial)).Checks);

        Assert.Equal(MatchStatus.NotCheckable, check.Status);
        Assert.Equal("char_location", check.CitationType);
        Assert.Contains("missing its quoted text or an offset", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableCitationsOfTheSameQuote_AreOneSource()
    {
        const string json =
            """{"type":"page_location","cited_text":"Sumps shall be liquid tight.","document_index":0,"document_title":null,"start_page_number":1,"end_page_number":2}""";

        QuoteReport report = Report([], Block("One.", RawCitation(json)), Block(" Two.", RawCitation(json)));

        Assert.Single(report.Checks);
        Assert.Equal(2, report.CitationCount);
        Assert.All(report.CitedBlocks, block => Assert.Equal([1], block.SourceNumbers));
    }

    // --- The whole answer -------------------------------------------------------

    [Fact]
    public void ASpanCitedTwice_IsCheckedAndCountedOnce()
    {
        AnswerCitation tight = CitePhrase(Tight);
        AnswerCitation tested = CitePhrase(Tested);

        QuoteReport report = Report([tight, tested], Block("Tight.", tight), Block(" Tested.", tight, tested, tested));

        Assert.Equal(2, report.Checks.Count);
        Assert.Equal(2, report.MatchedCount);
        Assert.Equal(4, report.CitationCount);
        Assert.Equal([[1], [1, 2]], report.CitedBlocks.Select(block => block.SourceNumbers));
    }

    [Fact]
    public void Failures_CarryTheirSourceNumberAndStatus()
    {
        AnswerCitation good = CitePhrase(Tight);
        AnswerCitation pastEnd = Cite(0, Section.Length + 1, "x");
        AnswerCitation wrong = CitePhrase(Tested) with { CitedText = "b) Sumps shall be tested weekly.." };

        QuoteReport report = Report([good, pastEnd, wrong], Block("Claims.", good, pastEnd, wrong));

        Assert.Equal(
            [(2, MatchStatus.OutOfRange), (3, MatchStatus.Mismatch)],
            report.Failures.Select(failure => (failure.SourceNumber, failure.Check.Status)));
        Assert.Equal(1, report.MatchedCount);
    }

    [Fact]
    public void Checks_FollowTheSourceOrderGiven()
    {
        AnswerCitation tight = CitePhrase(Tight);
        AnswerCitation tested = CitePhrase(Tested);

        QuoteReport report = Report([tested, tight], Block("Tight.", tight), Block(" Tested.", tested));

        Assert.Equal([tested, tight], report.Checks.Select(check => check.Citation));
        Assert.Equal([[2], [1]], report.CitedBlocks.Select(block => block.SourceNumbers));
    }

    [Fact]
    public void ACitationMissingFromTheSources_IsNumberedAfterThem()
    {
        AnswerCitation tight = CitePhrase(Tight);
        AnswerCitation tested = CitePhrase(Tested);

        QuoteReport report = Report([tight], Block("Both.", tight, tested));

        Assert.Equal([tight, tested], report.Checks.Select(check => check.Citation));
    }

    [Fact]
    public void OnlyTextBlocks_AreRead()
    {
        QuoteReport report = Report(
            [],
            new ThinkingBlock { Thinking = "Long private reasoning.", Signature = "sig" },
            Block("Sumps are tight.", CitePhrase(Tight)));

        Assert.Equal(Visible("Sumps are tight."), report.AnswerCharacters);
    }

    // --- Coverage ---------------------------------------------------------------

    [Fact]
    public void Coverage_LeavesOutWhitespace()
    {
        BlockCoverage block = Assert.Single(Report([], Block("Tight.", CitePhrase(Tight, extra: 2))).CitedBlocks);

        Assert.Equal(Visible(Tight), block.CitedCharacters);
        Assert.Equal(Visible("Tight."), block.BlockCharacters);
    }

    [Fact]
    public void OverlappingSpans_AreCountedOnce()
    {
        long start = Section.IndexOf(Tight, StringComparison.Ordinal);
        AnswerCitation whole = Cite(start, start + Tight.Length);
        AnswerCitation head = Cite(start, start + 15);
        AnswerCitation tail = Cite(start + 10, start + Tight.Length + "\n\nb) Sumps".Length);

        BlockCoverage block = Assert.Single(Report([], Block("Claim.", whole, head, tail)).CitedBlocks);

        Assert.Equal(Visible(Tight + "\n\nb) Sumps"), block.CitedCharacters);
    }

    [Fact]
    public void ASpanCitedInTwoBlocks_CountsForEach()
    {
        AnswerCitation tight = CitePhrase(Tight);

        QuoteReport report = Report([], Block("One.", tight), Block(" Two.", tight));

        Assert.Equal([Visible(Tight), Visible(Tight)], report.CitedBlocks.Select(block => block.CitedCharacters));
    }

    [Fact]
    public void AFailedSpan_SourcesNothing()
    {
        AnswerCitation good = CitePhrase(Tight);
        AnswerCitation wrong = CitePhrase(Tested) with { CitedText = "fabricated text here" };
        AnswerCitation pastEnd = Cite(0, Section.Length + 1, "x");

        BlockCoverage block = Assert.Single(Report([], Block("Claim.", good, wrong, pastEnd)).CitedBlocks);

        Assert.Equal(Visible(Tight), block.CitedCharacters);
    }

    [Fact]
    public void AnUnreadableCitation_SourcesNothing()
    {
        TextCitation page = RawCitation(
            """{"type":"page_location","cited_text":"x","document_index":0,"document_title":null,"start_page_number":1,"end_page_number":2}""");

        BlockCoverage block = Assert.Single(Report([], Block("A claim resting on nothing checkable.", page)).CitedBlocks);

        Assert.Equal(0, block.CitedCharacters);
        Assert.True(block.IsThin);
    }

    [Fact]
    public void ALongBlockOnAShortQuote_IsThin()
    {
        const string claim = "Sumps must be liquid tight, tested every year, and replaced as soon as cracked.";

        QuoteReport report = Report([], Block(claim, CitePhrase(Tight)));

        BlockCoverage block = Assert.Single(report.CitedBlocks);
        Assert.Equal((double)Visible(Tight) / Visible(claim), block.Fraction, 12);
        Assert.True(block.IsThin);
        Assert.Equal([block], report.ThinBlocks);
    }

    [Fact]
    public void AQuoteLongerThanItsBlock_CapsAtFull()
    {
        BlockCoverage block = Assert.Single(Report([], Block("Tight.", CitePhrase(Tight))).CitedBlocks);

        Assert.Equal(1.0, block.Fraction);
        Assert.False(block.IsThin);
    }

    [Fact]
    public void TheThinThreshold_IsExclusive()
    {
        double threshold = TestSettings.ValidOptions().ThinCoverageFraction;
        int atThreshold = (int)Math.Round(threshold * 100);

        Assert.False(new BlockCoverage("x", [1], 100, atThreshold, threshold).IsThin);
        Assert.True(new BlockCoverage("x", [1], 100, atThreshold - 1, threshold).IsThin);
    }

    [Fact]
    public void UncitedText_IsMeasuredAcrossTheAnswer()
    {
        QuoteReport report = Report(
            [],
            Block("The rules are:"),
            Block(" Sumps are tight.", CitePhrase(Tight)),
            Block(""));

        Assert.Equal(Visible("The rules are:") + Visible(" Sumps are tight."), report.AnswerCharacters);
        Assert.Equal(Visible("The rules are:"), report.UncitedCharacters);
        Assert.Equal((double)report.UncitedCharacters / report.AnswerCharacters, report.UncitedFraction, 12);
        Assert.Single(report.CitedBlocks);
    }

    [Fact]
    public void ACitedBlockWithNoVisibleText_IsCheckedButNotMeasured()
    {
        QuoteReport report = Report([], Block("  \n", CitePhrase(Tight)));

        Assert.Single(report.Checks);
        Assert.Empty(report.CitedBlocks);
    }

    [Fact]
    public void AnEmptyAnswer_HasNoUncitedShare()
    {
        QuoteReport report = Report([]);

        Assert.Empty(report.Checks);
        Assert.Equal(0.0, report.UncitedFraction);
    }

    // --- Code points (plan item 11) ---------------------------------------------

    [Fact]
    public void AfterANonBmpCharacter_CodePointOffsetsSliceTheQuote_AndUtf16OffsetsDoNot()
    {
        string text = "Figure " + SpikeFixtures.NonBmp + " is not reproduced.\n\n" + Tight + "\n\n" + Tested;
        CorpusSnapshot corpus = CorpusOf(text);
        int utf16Start = text.IndexOf(Tight, StringComparison.Ordinal);
        int codePointStart = utf16Start - 1;

        QuoteCheck byCodePoint = Checker.Check(new AnswerCitation(0, codePointStart, codePointStart + Tight.Length, Tight, Title), corpus);
        QuoteCheck byUtf16 = Checker.Check(new AnswerCitation(0, utf16Start, utf16Start + Tight.Length, Tight, Title), corpus);

        Assert.Equal(MatchStatus.Matched, byCodePoint.Status);
        Assert.Equal(MatchStatus.Mismatch, byUtf16.Status);
        Assert.Contains($"appear verbatim at character {codePointStart} (-1 from the cited start)", byUtf16.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEndAtTheDocumentsLength_Matches_AndOnePastIsOutOfRange(bool withNonBmp)
    {
        string text = "Figure " + (withNonBmp ? SpikeFixtures.NonBmp : "A") + " is not reproduced.\n\n" + Tight;
        CorpusSnapshot corpus = CorpusOf(text);
        int length = CodePoints(text);
        int start = length - Tight.Length;

        Assert.Equal(MatchStatus.Matched, Checker.Check(new AnswerCitation(0, start, length, Tight, Title), corpus).Status);
        QuoteCheck past = Checker.Check(new AnswerCitation(0, start, length + 1, Tight, Title), corpus);
        Assert.Equal(MatchStatus.OutOfRange, past.Status);
        Assert.Contains($"which has {length}", past.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndAtTheUtf16Length_IsOutOfRange_WhenThatIsPastTheCodePointLength()
    {
        string text = SpikeFixtures.NonBmp + " " + Tight;
        CorpusSnapshot corpus = CorpusOf(text);

        QuoteCheck check = Checker.Check(new AnswerCitation(0, 0, text.Length, text, Title), corpus);

        Assert.Equal(MatchStatus.OutOfRange, check.Status);
        Assert.Contains($"which has {text.Length - 1}", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDegenerateThreshold_CountsCodePoints()
    {
        string text = string.Concat(Enumerable.Repeat(SpikeFixtures.NonBmp, MinimumLength));
        CorpusSnapshot corpus = CorpusOf(text);
        string oneShort = string.Concat(Enumerable.Repeat(SpikeFixtures.NonBmp, MinimumLength - 1));

        // One short is still more UTF-16 units than the minimum, so a UTF-16 count would let it through.
        QuoteCheck shortCheck = Checker.Check(new AnswerCitation(0, 0, MinimumLength - 1, oneShort, Title), corpus);
        Assert.Equal(MatchStatus.Degenerate, shortCheck.Status);
        Assert.Contains($"the span has {MinimumLength - 1} non-whitespace characters", shortCheck.Detail, StringComparison.Ordinal);
        Assert.Equal(MatchStatus.Matched, Checker.Check(new AnswerCitation(0, 0, MinimumLength, text, Title), corpus).Status);
    }

    [Fact]
    public void MismatchPositionsAndLengths_CountCodePoints()
    {
        string pair = SpikeFixtures.NonBmp + SpikeFixtures.NonBmp;
        string text = pair + "bcdefghij and more";
        CorpusSnapshot corpus = CorpusOf(text);

        // The span holds 11 code points (13 UTF-16 units); the quote departs at code point 5 (UTF-16 unit 7).
        QuoteCheck departs = Checker.Check(new AnswerCitation(0, 0, 11, pair + "bcdXfghij", Title), corpus);
        Assert.Contains("departs from the source at character 5 of the span", departs.Detail, StringComparison.Ordinal);
        Assert.Contains("appears nowhere verbatim", departs.Detail, StringComparison.Ordinal);

        // A prefix of the span: it departs where it ends, and it appears at the cited start itself.
        QuoteCheck prefix = Checker.Check(new AnswerCitation(0, 0, 11, pair + "bcd", Title), corpus);
        Assert.Contains("departs from the source at character 5 of the span", prefix.Detail, StringComparison.Ordinal);
        Assert.Contains("the span is 11 characters and the quote 5", prefix.Detail, StringComparison.Ordinal);
        Assert.Contains("appear verbatim at character 0 (+0 from the cited start)", prefix.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteStartingWithALoneLowSurrogate_NeverMatchesHalfOfAPair()
    {
        string low = SpikeFixtures.NonBmp[1..];
        CorpusSnapshot corpus = CorpusOf("Figure " + SpikeFixtures.NonBmp + "xxxxxxxx and more");

        // A UTF-16 search finds the quote inside the pair; Python's code-point search finds nothing.
        QuoteCheck check = Checker.Check(new AnswerCitation(0, 0, 16, low + "xxxxxxxx", Title), corpus);

        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains("appears nowhere verbatim", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteEndingWithALoneHighSurrogate_NeverMatchesHalfOfAPair()
    {
        string high = SpikeFixtures.NonBmp[..1];
        CorpusSnapshot corpus = CorpusOf("yyyyyyyy" + SpikeFixtures.NonBmp + " more");

        QuoteCheck check = Checker.Check(new AnswerCitation(0, 0, 8, "yyyyyyyy" + high, Title), corpus);

        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains("the span is 8 characters and the quote 9", check.Detail, StringComparison.Ordinal);
        Assert.Contains("appears nowhere verbatim", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOccurrenceInsideAPair_IsSkipped_AndALaterWholeOneIsReported()
    {
        // The document also holds a lone low surrogate at code point 13, where the quote does occur whole.
        string low = SpikeFixtures.NonBmp[1..];
        CorpusSnapshot corpus = CorpusOf("x" + SpikeFixtures.NonBmp + "abcdefgh | " + low + "abcdefgh end");

        QuoteCheck check = Checker.Check(new AnswerCitation(0, 0, 10, low + "abcdefgh", Title), corpus);

        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains("appear verbatim at character 13 (+13 from the cited start)", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_CountsCodePoints()
    {
        string cited = string.Concat(Enumerable.Repeat(SpikeFixtures.NonBmp, 10));
        CorpusSnapshot corpus = CorpusOf(cited + " rest of the document");
        var citation = new AnswerCitation(0, 0, 10, cited, Title);

        QuoteReport report = Checker.BuildReport([citation], [Block(cited + cited, citation)], corpus);

        BlockCoverage block = Assert.Single(report.CitedBlocks);
        Assert.Equal(20, block.BlockCharacters);
        Assert.Equal(10, block.CitedCharacters);
        Assert.Equal(20, report.AnswerCharacters);
        Assert.False(block.IsThin);
    }

    // --- Wording ----------------------------------------------------------------

    [Fact]
    public void NoStatusDetailOrDisclaimer_ClaimsVerification()
    {
        AnswerCitation[] sources =
        [
            CitePhrase(Tight),
            CitePhrase(Tested) with { CitedText = "b) Sumps shall be tested weekly.." },
            Cite(0, 10, "Section 17") with { DocumentIndex = 7 },
            Cite(20, 10, ""),
            Cite(0, Section.Length + 1, "x"),
            Cite(10, 10, ""),
            Cite(0, 3),
        ];
        TextCitation page = RawCitation(
            """{"type":"page_location","cited_text":"x","document_index":0,"document_title":null,"start_page_number":1,"end_page_number":2}""");
        TextCitation partial = RawCitation(
            """{"type":"char_location","cited_text":"x","document_index":0,"document_title":null}""");

        QuoteReport report = Report(sources, Block("Claims.", [.. sources.Select(ToTextCitation), page, partial]));

        Assert.Equal(Enum.GetValues<MatchStatus>().ToHashSet(), report.Checks.Select(check => check.Status).ToHashSet());
        IEnumerable<string> wording = report.Checks.Select(check => check.Detail ?? "")
            .Concat(Enum.GetValues<MatchStatus>().Select(status => status.ToDisplayName()))
            .Append(QuoteChecker.MatchDisclaimer);
        Assert.All(wording, text => Assert.DoesNotContain("verif", text, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(MatchStatus.Matched, "matched")]
    [InlineData(MatchStatus.Mismatch, "mismatch")]
    [InlineData(MatchStatus.OutOfRange, "out of range")]
    [InlineData(MatchStatus.Degenerate, "degenerate")]
    [InlineData(MatchStatus.NotCheckable, "not checkable")]
    public void DisplayNames_AreThePrototypes(MatchStatus status, string expected) =>
        Assert.Equal(expected, status.ToDisplayName());

    // --- A streamed answer ------------------------------------------------------

    [Fact]
    public async Task AStreamedAnswersReport_LinesUpWithItsSourcesAndMarkers()
    {
        using var harness = new AnswerHarness(new StubHandler(_ => Sse.Response(CannedAnswer.Events)));
        AnswerResult result = await harness.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        CorpusSnapshot corpus = CorpusOf(SpikeFixtures.TextA, SpikeFixtures.TextB);

        QuoteReport report = Checker.BuildReport(result, corpus);

        Assert.Equal(result.Sources, report.Checks.Select(check => check.Citation));
        Assert.Equal(2, report.CitationCount);

        // The canned answer's offsets were written by hand and do not point at its quotes. The checker says
        // so, and names where each quote really is, in code points: the fixture's non-BMP character comes
        // before both, so a UTF-16 position would be one higher.
        Assert.Collection(
            report.Checks,
            check => AssertFoundElsewhere(check, CannedAnswer.CitedText1, 851),
            check => AssertFoundElsewhere(check, CannedAnswer.CitedText2, 950));
        BlockCoverage block = Assert.Single(report.CitedBlocks);
        Assert.Equal("a minimum of 5 gallons", block.Text);
        Assert.Equal([1, 2], block.SourceNumbers);
        Assert.Equal(Visible("Sumps must hold a minimum of 5 gallons."), report.AnswerCharacters);
        Assert.Equal(Visible("Sumps must hold ."), report.UncitedCharacters);
    }

    // --- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Asserts a mismatch whose detail names the quote's real position in the spike fixture's document A,
    /// counted here independently of the checker, and in code points.
    /// </summary>
    private static void AssertFoundElsewhere(QuoteCheck check, string quote, long citedStart)
    {
        string text = SpikeFixtures.TextA;
        int utf16 = text.IndexOf(quote, StringComparison.Ordinal);
        int actual = CodePoints(text[..utf16]);

        Assert.Equal(utf16 - 1, actual);
        Assert.True(actual > citedStart);
        Assert.Equal(MatchStatus.Mismatch, check.Status);
        Assert.Contains(
            $"the quote does appear verbatim at character {actual} (+{actual - citedStart} from the cited start)",
            check.Detail,
            StringComparison.Ordinal);
    }

    private static CorpusSnapshot CorpusOf(params string[] texts) => new(
        [.. texts.Select((text, i) => new CorpusDocument($"doc{i}.htm", new RequestDocument($"Document {i}", "", text), null))],
        "fingerprint",
        new Dictionary<string, long> { ["model"] = 1 });

    /// <summary>A citation of document 0 or 1 whose quote is the source text at the span unless given.</summary>
    private static AnswerCitation Cite(long start, long end, string? text = null, long document = 0) =>
        new(document, start, end, text ?? Documents.Documents[(int)document].Text[(int)start..(int)end], Title);

    /// <summary>A correct citation of <paramref name="phrase"/>, running <paramref name="extra"/> characters past it.</summary>
    private static AnswerCitation CitePhrase(string phrase, long document = 0, int extra = 0)
    {
        int start = Documents.Documents[(int)document].Text.IndexOf(phrase, StringComparison.Ordinal);
        return Cite(start, start + phrase.Length + extra, document: document);
    }

    private static TextBlock Block(string text) => new() { Text = text, Citations = null };

    private static TextBlock Block(string text, params AnswerCitation[] citations) =>
        Block(text, [.. citations.Select(ToTextCitation)]);

    private static TextBlock Block(string text, params TextCitation[] citations) =>
        new() { Text = text, Citations = citations.Length == 0 ? null : citations };

    private static QuoteReport Report(IReadOnlyList<AnswerCitation> sources, params ContentBlock[] content) =>
        Checker.BuildReport(sources, content, Documents);

    private static TextCitation ToTextCitation(AnswerCitation citation) => new(JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>
        {
            ["type"] = "char_location",
            ["cited_text"] = citation.CitedText,
            ["document_index"] = citation.DocumentIndex,
            ["document_title"] = citation.DocumentTitle,
            ["start_char_index"] = citation.StartCharIndex,
            ["end_char_index"] = citation.EndCharIndex,
        }));

    private static TextCitation RawCitation(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new TextCitation(document.RootElement.Clone());
    }

    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    /// <summary>Non-whitespace code points. The fixtures hold no U+001C to U+001F, where .NET and Python differ.</summary>
    private static int Visible(string text) => text.EnumerateRunes().Count(rune => !Rune.IsWhiteSpace(rune));
}
