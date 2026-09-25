using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// Reads and writes the corpus state file, which records the previous load's fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// A fingerprint that is shown and never compared catches nothing, so each load compares its corpus with
/// the recorded one and names what changed: an edited page, an AngleSharp upgrade, or an edit to a
/// cache-affecting value. Any of these means the next question pays the cache-write price.
/// </para>
/// <para>
/// A missing, unreadable or malformed file, including one with a field of the wrong type, is reported by
/// <see cref="Compare"/>, never thrown, and <see cref="Record"/> then replaces it.
/// </para>
/// </remarks>
public static class CorpusStateFile
{
    /// <summary>The layout version. A file with any other version is reported as unreadable, not guessed at.</summary>
    public const int Version = 1;

    /// <summary>Compares <paramref name="corpus"/> with the fingerprint recorded in <paramref name="path"/>.</summary>
    /// <param name="corpus">The corpus just assembled.</param>
    /// <param name="path">The state file.</param>
    /// <param name="parserVersion">This build's AngleSharp version, named when it differs from the recorded one.</param>
    /// <returns>The comparison. Never throws for a missing, unreadable or malformed file.</returns>
    public static FingerprintCheck Compare(Corpus corpus, string path, string parserVersion)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);

        string raw;
        try
        {
            raw = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return FingerprintCheck.FirstRun;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FingerprintCheck(FingerprintStatus.Unreadable, null, [$"{path}: {ex.Message}"]);
        }

        if (!TryParse(raw, out RecordedState? previous, out string? problem))
        {
            return new FingerprintCheck(FingerprintStatus.Unreadable, null, [$"{path}: {problem}"]);
        }

        return previous.Fingerprint == corpus.Fingerprint
            ? new FingerprintCheck(FingerprintStatus.Unchanged, previous.RecordedAt, [])
            : new FingerprintCheck(FingerprintStatus.Changed, previous.RecordedAt, DescribeChanges(previous, corpus, parserVersion));
    }

    /// <summary>Records <paramref name="corpus"/>'s fingerprint for the next load to compare with.</summary>
    /// <remarks>
    /// Written to a temporary file and moved into place, so an interrupted write cannot leave a truncated
    /// state file behind.
    /// </remarks>
    /// <param name="corpus">The corpus to record.</param>
    /// <param name="path">The state file.</param>
    /// <param name="parserVersion">This build's AngleSharp version.</param>
    /// <param name="recordedAt">When the corpus was loaded.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public static void Record(Corpus corpus, string path, string parserVersion, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";

        using (FileStream stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString(
                "recorded_at",
                recordedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            writer.WriteString("fingerprint", corpus.Fingerprint);

            writer.WriteStartObject("tokens");
            foreach ((string modelId, long tokens) in corpus.TokenCounts.OrderBy(count => count.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(modelId, tokens);
            }

            writer.WriteEndObject();
            writer.WriteString("anglesharp_version", parserVersion);

            writer.WriteStartArray("documents");
            foreach (CorpusDocument document in corpus.Documents)
            {
                writer.WriteStartObject();
                writer.WriteString("filename", document.FileName);
                writer.WriteString("label", document.Label);
                writer.WriteNumber("chars", document.CharacterCount);
                writer.WriteString("text_sha256", Sha256(document.Text));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        File.Move(temporary, fullPath, overwrite: true);
    }

    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static bool TryParse(
        string raw, [NotNullWhen(true)] out RecordedState? state, [NotNullWhen(false)] out string? problem)
    {
        state = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            problem = $"not valid JSON ({ex.Message})";
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetInt(root, "version", out int version)
                || version != Version)
            {
                problem = $"not a version-{Version} DocQaMs state file";
                return false;
            }

            if (!TryGetString(root, "fingerprint", out string? fingerprint)
                || !TryGetString(root, "recorded_at", out string? recordedAt)
                || !TryGetString(root, "anglesharp_version", out string? parserVersion)
                || !TryGetTokenCounts(root, out Dictionary<string, long>? tokens)
                || !TryGetDocuments(root, out Dictionary<string, RecordedDocument>? documents))
            {
                problem = "missing or malformed fields";
                return false;
            }

            state = new RecordedState(fingerprint, recordedAt, parserVersion, tokens, documents);
            problem = null;
            return true;
        }
    }

    private static bool TryGetString(JsonElement parent, string name, [NotNullWhen(true)] out string? value)
    {
        value = parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        return value is not null;
    }

    private static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static bool TryGetTokenCounts(JsonElement root, [NotNullWhen(true)] out Dictionary<string, long>? tokens)
    {
        tokens = null;
        if (!root.TryGetProperty("tokens", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt64(out long count)
                || !counts.TryAdd(property.Name, count))
            {
                return false;
            }
        }

        tokens = counts;
        return true;
    }

    // Keyed by file name. A hand-edited file that names one twice is malformed: which entry to compare
    // with would be a guess.
    private static bool TryGetDocuments(JsonElement root, [NotNullWhen(true)] out Dictionary<string, RecordedDocument>? documents)
    {
        documents = null;
        if (!root.TryGetProperty("documents", out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        Dictionary<string, RecordedDocument> byName = new(StringComparer.Ordinal);
        foreach (JsonElement entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryGetString(entry, "filename", out string? fileName)
                || !TryGetString(entry, "label", out string? label)
                || !TryGetInt(entry, "chars", out int chars)
                || !TryGetString(entry, "text_sha256", out string? textSha256)
                || !byName.TryAdd(fileName, new RecordedDocument(label, chars, textSha256)))
            {
                return false;
            }
        }

        documents = byName;
        return true;
    }

    private static List<string> DescribeChanges(RecordedState previous, Corpus corpus, string parserVersion)
    {
        Dictionary<string, CorpusDocument> current = corpus.Documents.ToDictionary(document => document.FileName, StringComparer.Ordinal);

        List<string> changes =
        [
            .. current.Keys.Except(previous.Documents.Keys).Order(StringComparer.Ordinal).Select(name => $"{name}: added"),
            .. previous.Documents.Keys.Except(current.Keys).Order(StringComparer.Ordinal).Select(name => $"{name}: removed"),
        ];

        foreach (string name in previous.Documents.Keys.Intersect(current.Keys).Order(StringComparer.Ordinal))
        {
            RecordedDocument before = previous.Documents[name];
            CorpusDocument after = current[name];
            if (before.Label != after.Label)
            {
                changes.Add($"{name}: label changed (\"{before.Label}\" -> \"{after.Label}\")");
            }

            if (before.TextSha256 != Sha256(after.Text))
            {
                changes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}: text changed ({before.Chars:N0} -> {after.CharacterCount:N0} characters)"));
            }
        }

        if (changes.Count == 0)
        {
            changes.Add(
                "no document's text or label changed, so the system prompt or a block setting did " +
                "(CacheAffecting.SystemPrompt, CacheAffecting.DocumentContextTemplate, CacheAffecting.CorpusCacheTtl, " +
                "or the block shape in MessageRequestBuilder and CorpusFingerprint)");
        }

        if (previous.ParserVersion != parserVersion)
        {
            changes.Add(
                $"AngleSharp {previous.ParserVersion} -> {parserVersion}: the likely cause of text changes if no " +
                "source file was edited");
        }

        foreach (string modelId in previous.Tokens.Keys.Union(corpus.TokenCounts.Keys).Order(StringComparer.Ordinal))
        {
            long? before = previous.Tokens.TryGetValue(modelId, out long recorded) ? recorded : null;
            long? after = corpus.TokenCounts.TryGetValue(modelId, out long counted) ? counted : null;
            if (before != after)
            {
                changes.Add($"tokens on {modelId}: {FormatCount(before)} -> {FormatCount(after)}");
            }
        }

        return changes;
    }

    private static string FormatCount(long? count) =>
        count is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : "not counted";

    private sealed record RecordedState(
        string Fingerprint,
        string RecordedAt,
        string ParserVersion,
        Dictionary<string, long> Tokens,
        Dictionary<string, RecordedDocument> Documents);

    private sealed record RecordedDocument(string Label, int Chars, string TextSha256);
}
