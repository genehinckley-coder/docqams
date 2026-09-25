using DocQaMs.Core.Tests.TestSupport;
using DocQaMs.Core.Text;

namespace DocQaMs.Core.Tests.Text;

/// <summary>Plan item 1, check 3: code-point offsets, converted to UTF-16, slice the right span.</summary>
public sealed class CodePointIndexTests
{
    private const string WithNonBmp = "ab" + SpikeFixtures.NonBmp + "cd";

    [Fact]
    public void TextWithoutSurrogatePairs_IsTheIdentity()
    {
        const string text = "Section\u00A0175.410 § — sumps";
        CodePointIndex index = CodePointIndex.Build(text);

        Assert.True(index.IsIdentity);
        Assert.Equal(text.Length, index.Length);
        for (int offset = 0; offset <= text.Length; offset++)
        {
            Assert.Equal(offset, index.ToUtf16(offset));
        }
    }

    [Fact]
    public void ANonBmpCharacter_ShiftsEveryLaterOffsetByOne()
    {
        CodePointIndex index = CodePointIndex.Build(WithNonBmp);

        Assert.False(index.IsIdentity);
        Assert.Equal(5, index.Length);
        Assert.Equal([0, 1, 2, 4, 5, 6], Enumerable.Range(0, 6).Select(index.ToUtf16));
        Assert.Equal("cd", Slice(WithNonBmp, index, 3, 5));
    }

    [Theory]
    [InlineData("abcd")]
    [InlineData(WithNonBmp)]
    public void TheExclusiveEndAtTheTextsLength_Converts(string text)
    {
        CodePointIndex index = CodePointIndex.Build(text);

        Assert.Equal(text.Length, index.ToUtf16(index.Length));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void OffsetsOutsideTheText_AreRefused(int offset)
    {
        // 6 is the UTF-16 length, but only 5 code points: bounds are checked in code points.
        CodePointIndex index = CodePointIndex.Build(WithNonBmp);

        Assert.Throws<ArgumentOutOfRangeException>(() => index.ToUtf16(offset));
    }

    [Fact]
    public void ALoneSurrogate_CountsAsOneCodePoint()
    {
        // A lone low surrogate, then a well-formed pair.
        string text = "a\uDC00" + SpikeFixtures.NonBmp + "b";
        CodePointIndex index = CodePointIndex.Build(text);

        Assert.Equal(4, index.Length);
        Assert.Equal(4, index.ToUtf16(3));
        Assert.Equal(5, index.ToUtf16(4));
    }

    [Fact]
    public void AnEmptyText_HasOnlyTheEndOffset()
    {
        CodePointIndex index = CodePointIndex.Build("");

        Assert.Equal(0, index.Length);
        Assert.Equal(0, index.ToUtf16(0));
    }

    [Fact]
    public void InTheSpikeFixture_ACodePointSpanAfterTheNonBmpCharacter_SlicesTheQuotedText()
    {
        string text = SpikeFixtures.TextA;
        const string quote = "minimum capacity of\n5\u00A0gallons";
        int utf16Start = text.IndexOf(quote, StringComparison.Ordinal);
        int start = text[..utf16Start].EnumerateRunes().Count();
        int end = start + quote.EnumerateRunes().Count();

        // The fixture's one non-BMP character comes before the quote, so the two offsets differ.
        Assert.Equal(utf16Start - 1, start);
        Assert.Equal(quote, Slice(text, CodePointIndex.Build(text), start, end));
    }

    [Theory]
    [InlineData("abcd")]
    [InlineData(WithNonBmp)]
    public void ToCodePoint_InvertsToUtf16_AtEveryBoundary(string text)
    {
        CodePointIndex index = CodePointIndex.Build(text);

        for (int offset = 0; offset <= index.Length; offset++)
        {
            Assert.Equal(offset, index.ToCodePoint(index.ToUtf16(offset)));
        }
    }

    [Fact]
    public void ToCodePoint_InsideASurrogatePair_ReturnsThePairsCodePoint()
    {
        // "ab", then the pair at UTF-16 units 2 and 3: unit 3 is its low half.
        Assert.Equal(2, CodePointIndex.Build(WithNonBmp).ToCodePoint(3));
    }

    [Theory]
    [InlineData("abcd", -1)]
    [InlineData("abcd", 5)]
    [InlineData(WithNonBmp, -1)]
    [InlineData(WithNonBmp, 7)]
    public void ToCodePoint_RefusesOffsetsOutsideTheText(string text, int utf16Offset)
    {
        // WithNonBmp is 6 UTF-16 units, so 6 converts and 7 does not.
        CodePointIndex index = CodePointIndex.Build(text);

        Assert.Throws<ArgumentOutOfRangeException>(() => index.ToCodePoint(utf16Offset));
    }

    private static string Slice(string text, CodePointIndex index, int start, int end)
    {
        int from = index.ToUtf16(start);
        return text[from..index.ToUtf16(end)];
    }
}
