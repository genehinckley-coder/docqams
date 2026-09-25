using System.Text;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Ports the prototype's tests/test_extract.py, plus checks specific to the C# parser. All offline.
/// The real-corpus and golden-file checks are in <see cref="CorpusGoldenTests"/>.
/// </summary>
public sealed class CorpusExtractorTests : IDisposable
{
    private const string Nbsp = "\u00A0";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("docqa-extract-tests-");

    public void Dispose() => _folder.Delete(recursive: true);

    // --- Paragraph structure ------------------------------------------------------------------------

    [Fact]
    public void SubsectionMarkers_SurviveAsSeparateParagraphs()
    {
        string nbsps = string.Concat(Enumerable.Repeat(Nbsp, 7));
        string html = Page(
            $"<p>a){nbsps} Applicability. This Section applies.</p>" +
            $"<p>1){nbsps} First item;</p>" +
            $"<p>A){nbsps} Nested item;</p>" +
            $"<p>ii){nbsps} Deepest item.</p>");

        Assert.Equal(
            "a) Applicability. This Section applies.\n\n" +
            "1) First item;\n\n" +
            "A) Nested item;\n\n" +
            "ii) Deepest item.",
            CorpusExtractor.ExtractHtml(html));
    }

    [Fact]
    public void SpacerParagraphs_LeaveExactlyOneBlankLine() =>
        Assert.Equal("One\n\nTwo", CorpusExtractor.ExtractHtml(Page($"<p>One</p><p>{Nbsp}</p><p></p><p> </p><p>Two</p>")));

    [Fact]
    public void HardWrappedSourceLines_JoinIntoOneLine() =>
        Assert.Equal(
            "The shell or head has any seam split",
            CorpusExtractor.ExtractHtml(Page("<p>The\r\nshell or head   has any\r\n  seam split</p>")));

    [Fact]
    public void CrlfInput_YieldsNoCarriageReturns()
    {
        string html = Page("<p>First\r\nparagraph</p><p>Second</p>").Replace("><", ">\r\n<", StringComparison.Ordinal);

        string text = CorpusExtractor.ExtractHtml(Encoding.UTF8.GetBytes(html));

        Assert.DoesNotContain('\r', text);
        Assert.Equal("First paragraph\n\nSecond", text);
    }

    [Fact]
    public void LiteralNbspAndNbspEntity_AreBothReplaced()
    {
        string text = CorpusExtractor.ExtractHtml(Page($"<p>at least 5{Nbsp}gallons and 3.5&nbsp;gallons</p>"));

        Assert.DoesNotContain(Nbsp, text, StringComparison.Ordinal);
        Assert.Equal("at least 5 gallons and 3.5 gallons", text);
    }

    // --- Refused text: stricter than the prototype, which had no such check --------------------------

    [Fact]
    public void InvalidUtf8_IsRefused_NotPassedOnAsReplacementCharacters()
    {
        byte[] html =
        [
            .. Encoding.ASCII.GetBytes("<html><head><meta charset=\"utf-8\"></head><body><div class=\"billtext-scale\"><p>caf"),
            0xE9,
            .. Encoding.ASCII.GetBytes("</p></div></body></html>"),
        ];

        var ex = Assert.Throws<CorpusExtractionException>(() => CorpusExtractor.ExtractHtml(html, "page.htm"));

        Assert.Contains("U+FFFD", ex.Message, StringComparison.Ordinal);
        Assert.Contains("page.htm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCharset_IsRefused_RatherThanGuessed()
    {
        // An unrecognized label falls back to UTF-8, where 0xE9 on its own is invalid.
        byte[] html =
        [
            .. Encoding.ASCII.GetBytes("<html><head><meta charset=\"x-bogus\"></head><body><div class=\"billtext-scale\"><p>caf"),
            0xE9,
            .. Encoding.ASCII.GetBytes("</p></div></body></html>"),
        ];

        Assert.Throws<CorpusExtractionException>(() => CorpusExtractor.ExtractHtml(html));
    }

    [Fact]
    public void AReplacementCharacterInThePageItself_IsAlsoRefused() =>
        // The check cannot tell a decoding failure from a page that really contains U+FFFD. No corpus
        // page does, and a regulation has no use for it.
        Assert.Throws<CorpusExtractionException>(() => CorpusExtractor.ExtractHtml(Page("<p>a&#xFFFD;b</p>")));

    [Fact]
    public void Br_IsALineBreakWithinAParagraph()
    {
        string html = Page(
            "<p class=\"jcarheading\">TITLE 41:  FIRE PROTECTION<br/>CHAPTER I:  OFFICE OF THE " +
            "STATE FIRE MARSHAL<br/>PART 175  TECHNICAL REQUIREMENTS</p><p>Next</p>");

        Assert.Equal(
            "TITLE 41: FIRE PROTECTION\n" +
            "CHAPTER I: OFFICE OF THE STATE FIRE MARSHAL\n" +
            "PART 175 TECHNICAL REQUIREMENTS\n\n" +
            "Next",
            CorpusExtractor.ExtractHtml(html));
    }

    [Fact]
    public void AParagraphHoldingOnlyABr_IsDropped() =>
        Assert.Equal("One\n\nTwo", CorpusExtractor.ExtractHtml(Page("<p>One</p><p class=\"content\"><br/></p><p>Two</p>")));

    [Fact]
    public void InlineMarkup_StaysOnTheLine()
    {
        string html = Page(
            "<p><b>Section 175.410</b> applies; a split of <sup>1</sup>/<sub>6</sub> of the " +
            "<a href=\"#x\">circumference</a> is <i>too</i> wide.</p>");

        Assert.Equal(
            "Section 175.410 applies; a split of 1/6 of the circumference is too wide.",
            CorpusExtractor.ExtractHtml(html));
    }

    [Fact]
    public void TextAfterABlockElement_StartsANewParagraph() =>
        Assert.Equal("Inside\n\nTail text\n\nAfter", CorpusExtractor.ExtractHtml(Page("<div><p>Inside</p>Tail text</div>After")));

    [Fact]
    public void ScriptStyleTemplateCommentsAndPageChrome_AreExcluded()
    {
        string html = Page(
            "<p>Rule<!-- editor note --> text</p>" +
            "<script>var leak = 1;</script><style>p { color: red }</style><template><p>Hidden</p></template>",
            chrome: "<header>Illinois General Assembly ILGA.GOV</header><h1>ADMINISTRATIVE CODE</h1>");

        Assert.Equal("Rule text", CorpusExtractor.ExtractHtml(html));
    }

    // --- Encoding -----------------------------------------------------------------------------------

    [Fact]
    public void Utf8Bytes_DecodeNonAsciiCharacters()
    {
        // The real pages declare UTF-8 in a meta tag, and file 175 contains characters such as U+2265.
        string html = "<html><head><meta http-equiv=\"content-type\" content=\"text/html; charset=UTF-8\"></head>" +
            "<body><div class=\"billtext-scale\"><p>pressure \u2265 5 psi, caf\u00E9</p></div></body></html>";

        Assert.Equal("pressure \u2265 5 psi, caf\u00E9", CorpusExtractor.ExtractHtml(Encoding.UTF8.GetBytes(html)));
    }

    [Fact]
    public void TheMetaCharset_IsHonored()
    {
        // 0xE9 is "\u00E9" in windows-1252, and invalid on its own in UTF-8.
        byte[] html =
        [
            .. Encoding.ASCII.GetBytes("<html><head><meta charset=\"windows-1252\"></head><body><div class=\"billtext-scale\"><p>caf"),
            0xE9,
            .. Encoding.ASCII.GetBytes("</p></div></body></html>"),
        ];

        Assert.Equal("caf\u00E9", CorpusExtractor.ExtractHtml(html));
    }

    // --- Content container --------------------------------------------------------------------------

    public static TheoryData<string> PagesWithoutExactlyOneContainer => new()
    {
        "<html><body><p>No container here</p></body></html>",
        Page("<p>One</p>").Replace("</body>", "<div class=\"billtext-scale\"><p>Two</p></div></body>", StringComparison.Ordinal),
    };

    [Theory]
    [MemberData(nameof(PagesWithoutExactlyOneContainer))]
    public void TheContentContainer_MustAppearExactlyOnce(string html)
    {
        var ex = Assert.Throws<CorpusExtractionException>(() => CorpusExtractor.ExtractHtml(html, "page.htm"));

        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
        Assert.Contains("page.htm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContainerSelectorInsideAScript_IsNotCounted() =>
        // Every real page has a second "billtext-scale", in a script that scales the container.
        Assert.Equal("Rule", CorpusExtractor.ExtractHtml(Page(
            "<p>Rule</p>",
            chrome: "<script>document.querySelector('#billtextanchor .billtext-scale');</script>")));

    [Fact]
    public void TheContainerClass_IsMatchedCaseSensitively() =>
        // As lxml's find_class is. A CSS selector would match this one too, because a page without a
        // doctype parses in quirks mode, where class selectors ignore case.
        Assert.Equal("Rule", CorpusExtractor.ExtractHtml(Page("<p>Rule</p>", chrome: "<div class=\"BILLTEXT-SCALE\">Other</div>")));

    [Fact]
    public void AContainerWithNoText_IsAnError()
    {
        var ex = Assert.Throws<CorpusExtractionException>(() => CorpusExtractor.ExtractHtml(Page($"<p>{Nbsp}</p><p><br/></p>")));

        Assert.Contains("no text", ex.Message, StringComparison.Ordinal);
    }

    // --- Tables -------------------------------------------------------------------------------------

    [Fact]
    public void ATable_IsItsOwnParagraph_WithOneLinePerRow()
    {
        string html = Page(
            "<p>Before</p>" +
            "<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>" +
            "<p>After</p>");

        Assert.Equal("Before\n\na | b\nc | d\n\nAfter", CorpusExtractor.ExtractHtml(html));
    }

    [Fact]
    public void EmptyCellsKeepTheirColumn_AndEmptyRowsAreDropped()
    {
        string html = Page(
            "<table><tbody>" +
            "<tr><th>Type</th><th>Permit</th><th>Notes</th></tr>" +
            $"<tr><td>{Nbsp}</td><td></td><td> </td></tr>" +
            "<tr><td>Removal</td><td></td><td>see (c)</td></tr>" +
            "<tr><td>Lining</td><td>Yes</td><td></td></tr>" +
            "</tbody></table>");

        Assert.Equal("Type | Permit | Notes\nRemoval |  | see (c)\nLining | Yes |", CorpusExtractor.ExtractHtml(html));
    }

    [Fact]
    public void TablesFromFile175_KeepRowAndColumnStructure()
    {
        // Real Word-derived markup: every cell wraps its text in <p>, some cells hold several, and the
        // source is CRLF with hard wraps inside cells.
        string fixture = RepoPaths.Combine("tests", "fixtures", "tables_175.html");
        Assert.Contains("\r\n", File.ReadAllText(fixture), StringComparison.Ordinal);

        Assert.Equal(
            "Nominal tank capacity | Whether use of manual tank gauging for release " +
            "detection is allowed | Time limit on use of manual tank gauging for release " +
            "detection\n" +
            "600 gallons or less | Allowed as sole method of release detection | " +
            "Allowed indefinitely\n" +
            "601-2,000 gallons | Not allowed | Not allowed\n" +
            "Over 2,000 gallons | Not allowed | Not allowed\n" +
            "\n" +
            "Nominal tank capacity | Weekly standard (one test) | Monthly standard " +
            "(average of 4 tests taken once weekly over a 4-week period)\n" +
            "600 gallons or less | 10 gallons | 5 gallons",
            CorpusExtractor.ExtractFile(fixture));
    }

    // --- Corpus discovery and labels ----------------------------------------------------------------

    [Fact]
    public void Discovery_ReadsTheTopLevelOnly_InOrdinalFileNameOrder()
    {
        foreach (string name in new[] { "b.htm", "a.html", "C.HTM", "notes.txt" })
        {
            File.WriteAllText(Path.Combine(_folder.FullName, name), "x");
        }

        Directory.CreateDirectory(Path.Combine(_folder.FullName, "a_files"));
        File.WriteAllText(Path.Combine(_folder.FullName, "a_files", "inner.htm"), "x");
        Directory.CreateDirectory(Path.Combine(_folder.FullName, "folder.htm"));

        IReadOnlyList<string> found = CorpusExtractor.DiscoverCorpusFiles(_folder.FullName);

        // Ordinal, as Python sorts: upper case before lower case.
        Assert.Equal(["C.HTM", "a.html", "b.htm"], found.Select(Path.GetFileName));
    }

    [Fact]
    public void AnHtmlFile_IsListedOnce()
    {
        // On Windows, the pattern "*.htm" also matches "page.html" through its 8.3 short name.
        File.WriteAllText(Path.Combine(_folder.FullName, "page.html"), "x");

        Assert.Single(CorpusExtractor.DiscoverCorpusFiles(_folder.FullName));
    }

    [Fact]
    public void TheCorpus_PairsEachFileWithItsLabel_InFileNameOrder()
    {
        WritePage("b.htm", "<p>Bravo</p>");
        WritePage("a.htm", "<p>Alpha</p>");

        IReadOnlyList<ExtractedDocument> documents =
            CorpusExtractor.ExtractCorpus(_folder.FullName, Labels(("a.htm", "Part A"), ("b.htm", "Part B")));

        Assert.Equal([new("a.htm", "Part A", "Alpha"), new("b.htm", "Part B", "Bravo")], documents);
    }

    [Fact]
    public void AnUnlabeledFile_IsAnError_NotAFallback()
    {
        WritePage("a.htm", "<p>Alpha</p>");
        WritePage("new.htm", "<p>New</p>");

        var ex = Assert.Throws<CorpusExtractionException>(
            () => CorpusExtractor.ExtractCorpus(_folder.FullName, Labels(("a.htm", "Part A"))));

        Assert.Matches(@"no label .*new\.htm", ex.Message);
    }

    [Fact]
    public void ALabeledFileMissingFromDisk_IsAnError()
    {
        WritePage("a.htm", "<p>Alpha</p>");

        var ex = Assert.Throws<CorpusExtractionException>(
            () => CorpusExtractor.ExtractCorpus(_folder.FullName, Labels(("a.htm", "Part A"), ("gone.htm", "Part G"))));

        Assert.Matches(@"not found .*gone\.htm", ex.Message);
    }

    [Fact]
    public void AnEmptyCorpusFolder_IsAnError()
    {
        var ex = Assert.Throws<CorpusExtractionException>(() => CorpusExtractor.ExtractCorpus(_folder.FullName, Labels()));

        Assert.Contains("No corpus files", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingCorpusFolder_IsAnError()
    {
        string missing = Path.Combine(_folder.FullName, "absent");

        var ex = Assert.Throws<CorpusExtractionException>(() => CorpusExtractor.ExtractCorpus(missing, Labels()));

        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A minimal ILGA-shaped page with <paramref name="content"/> inside the content container.</summary>
    private static string Page(string content, string chrome = "") =>
        "<html><head><title>ADMINISTRATIVE CODE</title></head><body>" +
        $"{chrome}<div class=\"billtext-scale\">{content}</div></body></html>";

    private static Dictionary<string, string> Labels(params (string File, string Label)[] entries) =>
        entries.ToDictionary(entry => entry.File, entry => entry.Label, StringComparer.Ordinal);

    private void WritePage(string name, string content) =>
        File.WriteAllText(Path.Combine(_folder.FullName, name), Page(content));
}
