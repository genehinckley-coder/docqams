using DocQaMs.Core.Answering;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;
using DocQaMs.Core.Text;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Plan item 9: where a cited span falls in a document's paragraphs, and the section headings that section navigation
/// uses.
/// </summary>
public sealed class CorpusDocumentTests
{
    // Paragraphs [0, 5) "Alpha", [7, 16) "Bravo two" and [18, 23) "Delta", with a blank line before each of the last two.
    private const string ThreeParagraphs = "Alpha\n\nBravo two\n\nDelta";

    // --- Highlights ---------------------------------------------------------------------------------

    [Fact]
    public void ASpanInsideOneParagraph_IsOnePiece_InThatParagraphsOwnOffsets() =>
        Assert.Equal([new ParagraphSpan(1, new TextRange(0, 5))], Document(ThreeParagraphs).FindHighlight(7, 12));

    [Fact]
    public void ASpanAcrossTheBlankLine_IsOnePiecePerParagraph_AndTheBlankLineIsInNeither() =>
        Assert.Equal(
            [new ParagraphSpan(0, new TextRange(2, 5)), new ParagraphSpan(1, new TextRange(0, 3))],
            Document(ThreeParagraphs).FindHighlight(2, 10));

    [Fact]
    public void ASpanOverThreeParagraphs_CoversTheMiddleOneWhole() =>
        Assert.Equal(
            [new ParagraphSpan(0, new TextRange(4, 5)), new ParagraphSpan(1, new TextRange(0, 9)), new ParagraphSpan(2, new TextRange(0, 1))],
            Document(ThreeParagraphs).FindHighlight(4, 19));

    [Fact]
    public void ASpanStartingInTheBlankLine_StartsAtTheNextParagraph() =>
        Assert.Equal([new ParagraphSpan(1, new TextRange(0, 5))], Document(ThreeParagraphs).FindHighlight(5, 12));

    [Fact]
    public void ASpanEndingAtTheEndOfTheText_Converts() =>
        Assert.Equal([new ParagraphSpan(2, new TextRange(0, 5))], Document(ThreeParagraphs).FindHighlight(18, 23));

    [Theory]
    [InlineData(5, 7)]
    [InlineData(3, 3)]
    [InlineData(9, 4)]
    [InlineData(-1, 4)]
    [InlineData(20, 24)]
    [InlineData(0, long.MaxValue)]
    [InlineData(long.MinValue, 2)]
    public void ASpanWithNothingToHighlight_GivesNoPieces_AndNeverThrows(long start, long end) =>
        Assert.Empty(Document(ThreeParagraphs).FindHighlight(start, end));

    [Fact]
    public void Offsets_AreCodePoints_EvenAfterACharacterOutsideTheBmp()
    {
        // In code points: the clef is 0, the blank line 1–2 and "Bravo" 3–8. In UTF-16 the clef takes 0–2.
        string clef = char.ConvertFromUtf32(0x1D11E);
        CorpusDocument document = Document($"{clef}\n\nBravo");

        Assert.False(document.CodePoints.IsIdentity);
        Assert.Equal([new ParagraphSpan(1, new TextRange(0, 5))], document.FindHighlight(3, 8));
        Assert.Equal([new ParagraphSpan(0, new TextRange(0, 2))], document.FindHighlight(0, 1));
        Assert.Empty(document.FindHighlight(3, 9));
    }

    [Fact]
    public void ASpanStartingAfterACharacterOutsideTheBmp_StartsWhereThatCharacterEnds()
    {
        // One paragraph, "x", the clef, "yz": code points 0–3, but UTF-16 offsets 0, 1–2, 3 and 4. Both ends of the
        // span fall inside the paragraph, so neither is clamped to its edge.
        CorpusDocument document = Document("x" + char.ConvertFromUtf32(0x1D11E) + "yz");

        Assert.Equal([new ParagraphSpan(0, new TextRange(3, 5))], document.FindHighlight(2, 4));
    }

    // --- Sections -----------------------------------------------------------------------------------

    [Fact]
    public void Sections_AreTheBodyHeadings_NotTheContentsEntries_EvenWhenWordedDifferently()
    {
        CorpusDocument document = Document(string.Join(
            "\n\n",
            "TITLE 41: FIRE PROTECTION",
            "Section 175.100 Definitions",
            "Section 175.APPENDIX A Activities",
            "AUTHORITY: Implementing the Act.",
            "Section 175.100 Definitions",
            "Terms mean what they say.",
            "Section 175.APPENDIX A Activities (Repealed)",
            "Rows."));

        Assert.Equal(
            [
                new SectionHeading(4, "175.100", "Section 175.100 Definitions"),
                new SectionHeading(6, "175.APPENDIX A", "Section 175.APPENDIX A Activities (Repealed)"),
            ],
            document.Sections);
    }

    [Fact]
    public void AHeadingWithNothingAfterItsNumber_IsASection() =>
        Assert.Equal([new SectionHeading(0, "178.100", "Section 178.100")], Document("Section 178.100").Sections);

    [Theory]
    [InlineData("Sections 175.410 and 175.420 apply.")]
    [InlineData("Section headings are listed first.")]
    [InlineData("Section 175. Sumps")]
    [InlineData(" Section 175.410 Sumps")]
    [InlineData("SUBPART A: DEFINITIONS")]
    public void AParagraphThatIsNotASectionHeading_IsNotASection(string paragraph) =>
        Assert.Empty(Document($"Intro.\n\n{paragraph}\n\nBody.").Sections);

    [Fact]
    public void FullWidthDigits_AreNotASectionNumber() =>
        Assert.Empty(Document("Section " + new string([(char)0xFF11, (char)0xFF17, (char)0xFF15]) + ".410 Sumps").Sections);

    [Theory]
    [InlineData("175", 48, "175.100", 61, "175.APPENDIX C", 1106)]
    [InlineData("176", 61, "176.100", 71, "176.APPENDIX A", 561)]
    [InlineData("178", 4, "178.100", 7, "178.130", 63)]
    public void OnTheRealParts_EachSectionAppearsOnce_AtItsBodyHeading(
        string part, int count, string firstNumber, int firstParagraph, string lastNumber, int lastParagraph)
    {
        // The expected figures were counted separately, in Python, from the same golden text.
        CorpusDocument document = Golden(part);

        Assert.Equal(count, document.Sections.Count);
        Assert.Equal((firstNumber, firstParagraph), (document.Sections[0].Number, document.Sections[0].Paragraph));
        Assert.Equal((lastNumber, lastParagraph), (document.Sections[^1].Number, document.Sections[^1].Paragraph));
        Assert.Equal(count, document.Sections.Select(section => section.Number).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(document.Sections.OrderBy(section => section.Paragraph), document.Sections);
        Assert.All(document.Sections, section =>
        {
            TextRange paragraph = document.Paragraphs[section.Paragraph];
            Assert.Equal(section.Heading, document.Text[paragraph.Start..paragraph.End]);
        });
    }

    [Fact]
    public void InPart176_ASectionListedInTheContents_PointsAtItsBodyHeading()
    {
        // "Section 176.590 Paper Hearings" is paragraph 52, in the contents, and paragraph 412, in the body.
        SectionHeading section = Assert.Single(Golden("176").Sections, section => section.Number == "176.590");

        Assert.Equal(412, section.Paragraph);
    }

    private static CorpusDocument Document(string text) => new("a.htm", new RequestDocument("Part A", "Source file: a.htm", text), null);

    private static CorpusDocument Golden(string part) =>
        Document(File.ReadAllText(RepoPaths.Combine("tests", "golden", $"{part}.txt")));
}
