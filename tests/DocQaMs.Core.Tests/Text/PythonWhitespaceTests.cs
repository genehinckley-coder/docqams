using DocQaMs.Core.Text;

namespace DocQaMs.Core.Tests.Text;

public sealed class PythonWhitespaceTests
{
    // Every code point for which chr(c).isspace() is true, from Python 3.13.14 (the prototype's
    // interpreter). The same set is exactly what re matches as \s in a str pattern; both were checked.
    private static readonly int[] PythonCodePoints =
    [
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x85, 0xA0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
        0x2028, 0x2029, 0x202F, 0x205F, 0x3000,
    ];

    [Fact]
    public void IsWhiteSpace_AgreesWithPython_ForEveryBmpCharacter()
    {
        // No code point above the BMP is whitespace to Python, so the char range covers every case.
        HashSet<int> expected = [.. PythonCodePoints];

        List<string> disagreements = Enumerable.Range(0, 0x10000)
            .Where(code => PythonWhitespace.IsWhiteSpace((char)code) != expected.Contains(code))
            .Select(code => $"U+{code:X4}")
            .ToList();

        Assert.Empty(disagreements);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" \t\r\n ", "")]
    [InlineData(" a  b ", "a b")]
    [InlineData("a\r\n\tb", "a b")]
    [InlineData("a\u00A0\u2003b", "a b")]
    [InlineData("\u001Fa\u001Cb\u001E", "a b")]
    [InlineData("a\u200Bb", "a\u200Bb")] // zero-width space is not whitespace to Python
    public void Collapse_MatchesPythonsJoinOfSplit(string input, string expected) =>
        Assert.Equal(expected, PythonWhitespace.Collapse(input));

    [Theory]
    [InlineData("", "")]
    [InlineData("Lining | Yes | ", "Lining | Yes |")]
    [InlineData(" a\u001D\u3000", " a")]
    [InlineData("a", "a")]
    public void TrimEnd_MatchesPythonsRstrip(string input, string expected) =>
        Assert.Equal(expected, PythonWhitespace.TrimEnd(input));
}
