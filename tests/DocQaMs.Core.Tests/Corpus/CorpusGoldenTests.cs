using System.Text;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Golden-file parity with the prototype, and the prototype's checks on the real corpus. The golden
/// files are the prototype's own extraction output (see tests/golden/README.md). All offline; extracting
/// the real corpus takes about a second.
/// </summary>
public sealed class CorpusGoldenTests
{
    private const string Nbsp = "\u00A0";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Extracted once and shared: the corpus is read-only and extraction is deterministic.
    private static readonly Lazy<IReadOnlyList<ExtractedDocument>> RealCorpus =
        new(() => CorpusExtractor.ExtractCorpus(RepoPaths.Combine("admincode")));

    public static TheoryData<string> Parts => new() { "172", "174", "175", "176", "177", "178" };

    // --- Golden parity ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Parts))]
    public void Extraction_IsByteIdenticalToThePrototypes(string part)
    {
        byte[] expected = File.ReadAllBytes(GoldenPath(part));
        string actual = Document(part).Text;

        if (!expected.AsSpan().SequenceEqual(StrictUtf8.GetBytes(actual)))
        {
            Assert.Fail(DescribeFirstDifference(StrictUtf8.GetString(expected), actual));
        }
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void GoldenFiles_AreUtf8WithNoBomAndNoCarriageReturn(string part)
    {
        byte[] bytes = File.ReadAllBytes(GoldenPath(part));

        Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), "The golden file starts with a UTF-8 BOM.");
        Assert.DoesNotContain((byte)0x0D, bytes);
        StrictUtf8.GetString(bytes);
    }

    [Fact]
    public void GoldenFiles_CoverExactlyTheLabeledParts()
    {
        IEnumerable<string> labeledParts = CacheAffecting.DocumentLabels.Keys
            .Select(name => Path.GetFileNameWithoutExtension(name)["ADMINISTRATIVE CODE 41 ".Length..]);
        IEnumerable<string> goldenParts = Directory.EnumerateFiles(RepoPaths.Combine("tests", "golden"), "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>();

        Assert.Equal(labeledParts.Order(StringComparer.Ordinal), goldenParts.Order(StringComparer.Ordinal));
        Assert.Equal(Parts.Select(row => row.Data), labeledParts.Order(StringComparer.Ordinal));
    }

    // --- The real corpus (the prototype's tests) ----------------------------------------------------

    [Fact]
    public void TheRealCorpus_MatchesTheConfiguredLabels() =>
        Assert.Equal(
            CacheAffecting.DocumentLabels.Keys.Order(StringComparer.Ordinal),
            RealCorpus.Value.Select(document => document.FileName));

    [Fact]
    public void TheRealCorpus_IsNormalized() =>
        Assert.All(RealCorpus.Value, document =>
        {
            Assert.DoesNotContain('\r', document.Text);
            Assert.DoesNotContain(Nbsp, document.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\n\n\n", document.Text, StringComparison.Ordinal);
            Assert.All(document.Text.Split('\n'), line => Assert.Equal(line.Trim(), line));
        });

    [Fact]
    public void TheRealCorpus_StartsAtTheRegulation_NotTheSiteChrome() =>
        Assert.All(RealCorpus.Value, document => Assert.StartsWith(
            "TITLE 41: FIRE PROTECTION\nCHAPTER I: OFFICE OF THE STATE FIRE MARSHAL\nPART ",
            document.Text,
            StringComparison.Ordinal));

    [Fact]
    public void File175_KeepsTheSumpSectionAndItsTables()
    {
        string text = Document("175").Text;
        const string heading = "Section 175.410 Submersible, Under-dispenser, Transition and Other Containment Sumps";

        // Once in the table of contents, once heading the Section itself.
        Assert.True(CountOccurrences(text, $"\n\n{heading}\n\n") >= 2, "The sump Section heading appears fewer than twice.");
        Assert.Contains(
            "\n600 gallons or less | Allowed as sole method of release detection | Allowed indefinitely\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("1/6 of the circumference of the tank", text, StringComparison.Ordinal);
    }

    private static ExtractedDocument Document(string part) =>
        RealCorpus.Value.Single(document => document.FileName == $"ADMINISTRATIVE CODE 41 {part}.htm");

    private static string GoldenPath(string part) => RepoPaths.Combine("tests", "golden", $"{part}.txt");

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // A whole-document Assert.Equal on 250K characters is unreadable; this names the first difference
    // and shows it in context, with line breaks made visible.
    private static string DescribeFirstDifference(string expected, string actual)
    {
        int index = 0;
        int limit = Math.Min(expected.Length, actual.Length);
        while (index < limit && expected[index] == actual[index])
        {
            index++;
        }

        return $"First difference at character {index} (golden length {expected.Length}, extracted length {actual.Length}).\n" +
            $"Golden:    {Excerpt(expected, index)}\n" +
            $"Extracted: {Excerpt(actual, index)}";
    }

    private static string Excerpt(string text, int index)
    {
        int start = Math.Max(0, index - 80);
        int end = Math.Min(text.Length, index + 80);
        return text[start..end].Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
