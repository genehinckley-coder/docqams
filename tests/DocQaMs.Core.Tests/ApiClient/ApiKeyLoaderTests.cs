using System.Text;
using DocQaMs.Core.ApiClient;

namespace DocQaMs.Core.Tests.ApiClient;

/// <summary>
/// Ports the prototype's tests/test_credentials.py. None of these reads the real key: every key file
/// is written to a temporary folder.
/// </summary>
public sealed class ApiKeyLoaderTests : IDisposable
{
    private const string Key = "sk-ant-api03-kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";
    private const string OtherKey = "sk-ant-api03-oooooooooooooooooooooooooooooooooooooooo";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("docqa-key-tests-");

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public void ABareKey_IsReturned() =>
        Assert.Equal(Key, ApiKeyLoader.Load(KeyFile(Encoding.UTF8.GetBytes(Key))));

    [Fact]
    public void ANotepadBomAndTrailingCrlf_AreStripped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Key + "\r\n")];

        Assert.Equal(Key, ApiKeyLoader.Load(KeyFile(bytes)));
    }

    [Fact]
    public void AnnotationLinesAroundTheKey_AreIgnored() =>
        // The real key file carries a hand-written expiry note above the key.
        Assert.Equal(Key, ApiKeyLoader.Load(KeyFile($"Expires 2027-01-31, rotate before then\r\n{Key}\r\n\r\n")));

    [Theory]
    [InlineData(0x0D)] // CR
    [InlineData(0x0B)] // vertical tab
    [InlineData(0x0C)] // form feed
    [InlineData(0x1C)] // file separator
    [InlineData(0x85)] // NEL
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    public void PythonsLineBoundaries_SeparateTheKeyFromAnnotations(int boundaryCode)
    {
        // The prototype splits with str.splitlines(), which recognizes all of these.
        string boundary = ((char)boundaryCode).ToString();

        Assert.Equal(Key, ApiKeyLoader.Load(KeyFile($"Expires 2027-01-31{boundary}{Key}{boundary}")));
    }

    [Fact]
    public void AMissingFile_NamesThePath()
    {
        string path = Path.Combine(_folder.FullName, "absent.txt");

        var ex = Assert.Throws<FileNotFoundException>(() => ApiKeyLoader.Load(path));

        Assert.Contains("absent.txt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFolder_IsReportedAsAMissingFile() =>
        Assert.Throws<FileNotFoundException>(() => ApiKeyLoader.Load(Path.Combine(_folder.FullName, "gone", "key.txt")));

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t\r\n")]
    public void AnEmptyFile_IsRefused(string content)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ApiKeyLoader.Load(KeyFile(content)));

        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithNoKeyLine_IsRefusedWithoutEchoingIt()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ApiKeyLoader.Load(KeyFile("api03-looks-secret\nanother line\n")));

        Assert.Contains("No Anthropic API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("looks-secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoKeyLines_AreAmbiguous_AndNeitherIsEchoed()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ApiKeyLoader.Load(KeyFile($"{Key}\n{OtherKey}\n")));

        Assert.Contains("2 lines", ex.Message, StringComparison.Ordinal);
        AssertNoKeyText(ex);
    }

    [Theory]
    [InlineData(0x20)] // space
    [InlineData(0x09)] // tab
    [InlineData(0xA0)] // no-break space
    [InlineData(0x00)] // NUL
    [InlineData(0x1F)] // unit separator
    [InlineData(0xE9)] // é
    public void AKeyWithWhitespaceControlOrNonAsciiCharacters_IsRefusedWithoutEchoingIt(int intruderCode)
    {
        string broken = Key[..20] + (char)intruderCode + Key[20..];

        var ex = Assert.Throws<InvalidDataException>(() => ApiKeyLoader.Load(KeyFile(broken)));

        Assert.Contains("printable ASCII", ex.Message, StringComparison.Ordinal);
        AssertNoKeyText(ex);
    }

    [Fact]
    public void InvalidUtf8_IsRefused_AndTheDecoderMessageIsDropped()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes(Key[..20]), 0xFF, .. Encoding.UTF8.GetBytes(Key[20..])];

        var ex = Assert.Throws<InvalidDataException>(() => ApiKeyLoader.Load(KeyFile(bytes)));

        Assert.Contains("not valid UTF-8", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    private static void AssertNoKeyText(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            Assert.DoesNotContain("kkkk", current.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("oooo", current.Message, StringComparison.Ordinal);
        }
    }

    private string KeyFile(string content) => KeyFile(Encoding.UTF8.GetBytes(content));

    // Written byte for byte, so CRLF and a BOM survive.
    private string KeyFile(byte[] bytes)
    {
        string path = Path.Combine(_folder.FullName, "notrag_key.txt");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
