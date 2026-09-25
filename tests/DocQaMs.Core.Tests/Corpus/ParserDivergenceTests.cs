using System.Text;
using DocQaMs.Core.Corpus;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Pins where this port's HTML5 parser (AngleSharp) and the prototype's libxml2 parser (lxml) produce
/// different text.
/// </summary>
/// <remarks>
/// None of these constructs occurs in the six corpus pages, whose extraction is byte-identical to the
/// prototype's (<see cref="CorpusGoldenTests"/>); they matter only if a page changes. Each case follows
/// HTML5, which is what a browser shows a reader of the page, and each was accepted consciously (user
/// decision, 2026-09-14; plan item 2). The prototype's output, measured with lxml 6.1.3 and libxml2
/// 2.11.9, is given with each case.
/// </remarks>
public sealed class ParserDivergenceTests
{
    [Fact]
    public void TextDirectlyInsideATable_IsKept_BeforeTheTable() =>
        // Prototype: "x". HTML5 moves ("foster-parents") the text out of the table; lxml leaves it
        // inside, where the table renderer never reads it.
        Assert.Equal("orphan\n\nx", Extract("<table>orphan<tr><td>x</td></tr></table>"));

    [Fact]
    public void TextBetweenTableRows_IsKept_BeforeTheTable() =>
        // Prototype: "a\nb".
        Assert.Equal("stray\n\na\nb", Extract("<table><tr><td>a</td></tr>stray<tr><td>b</td></tr></table>"));

    [Fact]
    public void AStrayBrEndTag_IsALineBreak() =>
        // Prototype: "ab". HTML5 treats </br> as <br>.
        Assert.Equal("a\nb", Extract("<p>a</br>b</p>"));

    [Fact]
    public void AStrayPEndTag_IsAParagraphBreak() =>
        // Prototype: "a\n\nbc". HTML5 turns an unmatched </p> into an empty paragraph.
        Assert.Equal("a\n\nb\n\nc", Extract("<p>a</p>b</p>c"));

    [Fact]
    public void LegacyEntitiesWithoutASemicolon_AreDecoded() =>
        // Prototype: "a&ampb &copy2026 &nbspx &notit;", left as written.
        Assert.Equal("a&b \u00A92026 x \u00ACit;", Extract("<p>a&ampb &copy2026 &nbspx &notit;</p>"));

    [Fact]
    public void CharacterReferencesFrom128To159_MapToWindows1252() =>
        // Prototype: the C1 control characters U+0080 and U+0096.
        Assert.Equal("price\u20AC5 \u2013 dash", Extract("<p>price&#128;5 &#150; dash</p>"));

    [Fact]
    public void ANulCharacterReference_IsRefused() =>
        // Prototype: dropped, giving "ab". HTML5 decodes &#0; to U+FFFD, which extraction refuses.
        Assert.Throws<CorpusExtractionException>(() => Extract("<p>a&#0;b</p>"));

    [Theory]
    [InlineData(0x0C)] // form feed
    [InlineData(0x1C)] // file separator
    [InlineData(0x1D)] // group separator
    [InlineData(0x1E)] // record separator
    [InlineData(0x1F)] // unit separator
    public void RawWhitespaceControlCharacters_BecomeSpaces(int code)
    {
        // Prototype: removed, giving "ab". libxml2 strips characters that are illegal in XML before the
        // prototype's whitespace pattern sees them. Here they reach it, and are whitespace to Python's
        // \s (see PythonWhitespace).
        string c = ((char)code).ToString();

        Assert.Equal("a b", Extract($"<p>{c}a{c}{c}b{c}</p>"));
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x08)]
    [InlineData(0x7F)]
    public void OtherRawControlCharacters_AreKept(int code)
    {
        // Prototype: U+0001 and U+0008 removed, for the same reason as above; U+007F kept, since XML
        // allows it.
        string c = ((char)code).ToString();

        Assert.Equal($"a{c}b", Extract($"<p>a{c}b</p>"));
    }

    private static string Extract(string content) =>
        CorpusExtractor.ExtractHtml(Encoding.UTF8.GetBytes(
            "<html><head><meta charset=\"utf-8\"></head><body><div class=\"billtext-scale\">" + content + "</div></body></html>"));
}
