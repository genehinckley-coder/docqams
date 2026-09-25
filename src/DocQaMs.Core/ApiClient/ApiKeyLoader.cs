using System.Text;

namespace DocQaMs.Core.ApiClient;

/// <summary>
/// Reads and validates the Anthropic API key from a file outside the repository.
/// </summary>
/// <remarks>
/// Ported from the NotRag prototype's <c>credentials.py</c>. The easy ways to load a credential are
/// the insecure ones, so the rules are worth stating:
/// <list type="bullet">
/// <item>The key is read from a file and handed to the client explicitly. It is never placed in an
/// environment variable, which every child process would inherit.</item>
/// <item>The key is never logged or included in an exception message. Failures name the file, never
/// its contents.</item>
/// </list>
/// </remarks>
public static class ApiKeyLoader
{
    /// <summary>The prefix every Anthropic API key starts with.</summary>
    /// <remarks>
    /// Checked so that pointing at the wrong file fails immediately with a useful message, rather than
    /// later as an opaque 401 from the API.
    /// </remarks>
    public const string KeyPrefix = "sk-ant-";

    // Throws on invalid bytes instead of substituting U+FFFD, which would pass every check below and
    // surface later as an authentication failure.
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The line boundaries of Python's str.splitlines(), which the prototype uses, so a file the
    // prototype accepts is accepted here too: CRLF (first, so it splits as one boundary), LF, CR,
    // vertical tab, form feed, the file, group and record separators, NEL, and the Unicode line and
    // paragraph separators.
    private static readonly string[] LineBoundaries =
    [
        "\r\n", "\n", "\r",
        .. new[] { 0x0B, 0x0C, 0x1C, 0x1D, 0x1E, 0x85, 0x2028, 0x2029 }.Select(code => ((char)code).ToString()),
    ];

    /// <summary>
    /// Reads the key file and returns the one key in it.
    /// </summary>
    /// <remarks>
    /// The file may contain annotation lines alongside the key: these files are maintained by hand and
    /// often carry an expiry note. Exactly one line must start with <see cref="KeyPrefix"/>; every
    /// other line is ignored.
    /// </remarks>
    /// <param name="path">The key file.</param>
    /// <returns>The key, without a byte-order mark or surrounding whitespace.</returns>
    /// <exception cref="FileNotFoundException">The key file, or its folder, does not exist.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not valid UTF-8, is empty, has no key line or more than one, or its key contains
    /// anything other than printable ASCII.
    /// </exception>
    public static string Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string raw;
        try
        {
            // ReadAllText detects and strips a UTF-8 BOM, which Notepad adds silently and which would
            // otherwise ride along into the x-api-key header.
            raw = File.ReadAllText(path, StrictUtf8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException(
                $"API key file not found: {path}. Create it containing the key, or point " +
                $"DocQaMs:ApiKeyPath somewhere else.",
                path,
                ex);
        }
        catch (DecoderFallbackException)
        {
            // The inner exception is dropped on purpose: its message quotes the offending bytes.
            throw new InvalidDataException($"API key file is not valid UTF-8: {path}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidDataException($"API key file is empty: {path}");
        }

        // Trimming each line also removes a trailing "\r", which is invalid in an HTTP header value and
        // would surface as a confusing authentication error rather than as a file problem.
        List<string> candidates = raw.Split(LineBoundaries, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                $"No Anthropic API key found in {path} (expected a line starting with '{KeyPrefix}'). " +
                $"Check that DocQaMs:ApiKeyPath points at the right file.");
        }

        if (candidates.Count > 1)
        {
            throw new InvalidDataException(
                $"{path} contains {candidates.Count} lines that look like API keys. Leave exactly one, " +
                $"so there is no ambiguity about which is current.");
        }

        // A real key is printable ASCII with no spaces. Anything else is invalid in an HTTP header,
        // and would otherwise fail later, far from the file, as a header or authentication error.
        string key = candidates[0];
        if (key.Any(c => c is <= ' ' or > '~'))
        {
            throw new InvalidDataException(
                $"The API key in {path} contains whitespace, a control character or a non-ASCII " +
                $"character. Expected the key on a single unbroken line of printable ASCII.");
        }

        return key;
    }
}
