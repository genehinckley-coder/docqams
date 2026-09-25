using System.Collections.Frozen;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Text;

namespace DocQaMs.Core.Corpus;

/// <summary>
/// Extracts the regulation text from the saved ILGA pages, keeping the paragraph and subsection
/// structure that makes the corpus answerable.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the NotRag prototype's <c>extract.py</c>. The output must equal the prototype's byte for
/// byte (<c>tests/golden</c>): the prototype's live measurements, and its 42-of-42 matched quotes on the
/// sump question, were taken on exactly that text.
/// </para>
/// <para>
/// The obvious one-liner, collapsing all whitespace, turns every Section into one wall of text and
/// loses the <c>a)</c>, <c>1)</c>, <c>A)</c>, <c>i)</c> subsection markers. So structure is kept, and
/// whitespace is collapsed only within a line:
/// </para>
/// <list type="bullet">
/// <item>Each paragraph-level element becomes one paragraph. Paragraphs are separated by one blank line,
/// and empty ones are dropped, including ILGA's U+00A0-only spacer paragraphs.</item>
/// <item><c>&lt;br&gt;</c> is a line break inside a paragraph.</item>
/// <item>A table is one paragraph: one line per row, cells joined by <c>" | "</c>. A cell's content is
/// flattened onto its row's line.</item>
/// <item>Inside a line, every whitespace run becomes one space. That covers the source's CRLF hard
/// wraps and U+00A0.</item>
/// </list>
/// <para>
/// Only ILGA's content container (<c>div.billtext-scale</c>) is read. If it is missing or duplicated,
/// extraction fails rather than falling back to the whole page, which would pull site chrome into the
/// corpus.
/// </para>
/// <para>
/// The prototype parses with libxml2; this port parses with AngleSharp, which follows HTML5. On
/// malformed markup that no corpus page contains, such as text directly inside a table or a stray
/// <c>&lt;/br&gt;</c>, the two build different trees, and this port produces what a browser shows.
/// Each such case is pinned in <c>ParserDivergenceTests</c>. Unlike the prototype, text containing
/// U+FFFD is refused: it means part of the page did not decode.
/// </para>
/// </remarks>
public static class CorpusExtractor
{
    /// <summary>Class of the one element on an ILGA page that holds the regulation text.</summary>
    public const string ContentClass = "billtext-scale";

    private const string CellSeparator = " | ";

    // Compared case-insensitively, as Python's glob is on Windows. Filtering an enumeration of "*"
    // avoids a Windows quirk: the pattern "*.htm" also matches ".html" files through their 8.3 short
    // names, which would list them twice.
    private static readonly FrozenSet<string> CorpusExtensions =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".htm", ".html");

    // Elements whose content is never shown to a reader.
    private static readonly FrozenSet<string> StrippedTags =
        FrozenSet.Create(StringComparer.Ordinal, "script", "style", "template");

    // Elements that open and close a paragraph. Anything else, apart from br and table, is inline: its
    // text joins the surrounding line. The prototype's set, verbatim.
    private static readonly FrozenSet<string> BlockTags = FrozenSet.Create(
        StringComparer.Ordinal,
        "address", "article", "aside", "blockquote", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3",
        "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre",
        "section", "ul",
        // Table parts found outside a <table> (malformed markup) degrade to paragraphs rather than
        // running together.
        "caption", "tbody", "td", "tfoot", "th", "thead", "tr");

    /// <summary>
    /// The corpus files at the top level of <paramref name="corpusDirectory"/>, sorted by file name.
    /// </summary>
    /// <remarks>
    /// Top level only: the browser's sibling <c>*_files</c> folders hold CSS, scripts and images, never
    /// regulation text. The sort is ordinal, as Python's is, so documents never reorder by culture.
    /// </remarks>
    /// <param name="corpusDirectory">The corpus folder.</param>
    /// <returns>Full paths of the <c>.htm</c> and <c>.html</c> files, each listed once.</returns>
    /// <exception cref="CorpusExtractionException">The folder does not exist.</exception>
    public static IReadOnlyList<string> DiscoverCorpusFiles(string corpusDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusDirectory);

        if (!Directory.Exists(corpusDirectory))
        {
            throw new CorpusExtractionException($"Corpus folder not found: {corpusDirectory}");
        }

        return Directory.EnumerateFiles(corpusDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => CorpusExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Extracts every corpus file in file-name order, each paired with its label from
    /// <see cref="CacheAffecting.DocumentLabels"/>.
    /// </summary>
    /// <inheritdoc cref="ExtractCorpus(string, IReadOnlyDictionary{string, string})"/>
    public static IReadOnlyList<ExtractedDocument> ExtractCorpus(string corpusDirectory) =>
        ExtractCorpus(corpusDirectory, CacheAffecting.DocumentLabels);

    /// <summary>
    /// Extracts every corpus file in file-name order, each paired with its label.
    /// </summary>
    /// <remarks>
    /// The files on disk and the labels must agree exactly. An unlabeled file has no acceptable
    /// fallback title: labels are cache-affecting, so a file name standing in for one would change
    /// later. A label with no file means the corpus is incomplete, and the app would answer confidently
    /// from a partial source.
    /// </remarks>
    /// <param name="corpusDirectory">The corpus folder.</param>
    /// <param name="labels">The citation label of each corpus file, keyed by file name.</param>
    /// <returns>One document per corpus file, sorted by file name.</returns>
    /// <exception cref="CorpusExtractionException">
    /// The folder is missing or has no corpus files, the files and labels disagree, or a page could not
    /// be extracted.
    /// </exception>
    /// <exception cref="IOException">A corpus file could not be read.</exception>
    public static IReadOnlyList<ExtractedDocument> ExtractCorpus(
        string corpusDirectory, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        IReadOnlyList<string> paths = DiscoverCorpusFiles(corpusDirectory);
        if (paths.Count == 0)
        {
            throw new CorpusExtractionException($"No corpus files (*.htm, *.html) found in {corpusDirectory}");
        }

        HashSet<string> names = paths.Select(path => Path.GetFileName(path)).ToHashSet(StringComparer.Ordinal);
        List<string> problems = [];

        List<string> unlabeled = names.Where(name => !labels.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        if (unlabeled.Count > 0)
        {
            problems.Add($"no label in CacheAffecting.DocumentLabels for: {string.Join(", ", unlabeled)}");
        }

        List<string> missing = labels.Keys.Where(name => !names.Contains(name)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"labeled but not found in {corpusDirectory}: {string.Join(", ", missing)}");
        }

        if (problems.Count > 0)
        {
            throw new CorpusExtractionException("Corpus files and labels disagree: " + string.Join("; ", problems));
        }

        return paths
            .Select(path => new ExtractedDocument(Path.GetFileName(path), labels[Path.GetFileName(path)], ExtractFile(path)))
            .ToList();
    }

    /// <summary>Extracts the regulation text from one saved ILGA page.</summary>
    /// <param name="path">The page.</param>
    /// <returns>The page's regulation text; see <see cref="ExtractHtml(byte[], string)"/>.</returns>
    /// <exception cref="CorpusExtractionException">The page could not be extracted.</exception>
    /// <exception cref="IOException">The page could not be read.</exception>
    public static string ExtractFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Bytes, not a string, so the page's own meta charset is honored.
        return ExtractHtml(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Extracts the regulation text from one ILGA page's bytes.</summary>
    /// <remarks>
    /// The encoding comes from the bytes: a byte-order mark, then the page's meta charset. The parser
    /// has no loader, so it never fetches a resource or runs a script.
    /// </remarks>
    /// <param name="html">The page.</param>
    /// <param name="source">The name used in error messages.</param>
    /// <returns>
    /// Paragraphs separated by one blank line, with tables as <c>" | "</c>-joined rows. Contains no CR
    /// and no U+00A0.
    /// </returns>
    /// <exception cref="CorpusExtractionException">
    /// The page does not have exactly one content container, the container yields no text, or the text
    /// contains U+FFFD because part of the page did not decode.
    /// </exception>
    public static string ExtractHtml(byte[] html, string source = "<html>")
    {
        ArgumentNullException.ThrowIfNull(html);

        using var stream = new MemoryStream(html, writable: false);
        using IHtmlDocument document = new HtmlParser().ParseDocument(stream);
        return Extract(document, source);
    }

    /// <summary>Extracts the regulation text from one ILGA page that is already decoded.</summary>
    /// <param name="html">The page.</param>
    /// <param name="source">The name used in error messages.</param>
    /// <returns>The page's regulation text; see <see cref="ExtractHtml(byte[], string)"/>.</returns>
    /// <exception cref="CorpusExtractionException">
    /// The page does not have exactly one content container, the container yields no text, or the text
    /// contains U+FFFD because part of the page did not decode.
    /// </exception>
    public static string ExtractHtml(string html, string source = "<html>")
    {
        ArgumentNullException.ThrowIfNull(html);

        using IHtmlDocument document = new HtmlParser().ParseDocument(html);
        return Extract(document, source);
    }

    private static string Extract(IDocument document, string source)
    {
        // ClassList matching is case-sensitive, as lxml's find_class is. A CSS class selector would not
        // be: it ignores case in quirks mode, which any page without a doctype gets. Script text is not
        // markup, so the selector string in each page's own script is never counted.
        List<IElement> containers = document.All.Where(element => element.ClassList.Contains(ContentClass)).ToList();
        if (containers.Count != 1)
        {
            throw new CorpusExtractionException(
                $"{source}: expected exactly one element with class '{ContentClass}', found " +
                $"{containers.Count}. The ILGA page layout may have changed; extraction does not fall " +
                $"back to the whole page, which would pull site chrome into the corpus.");
        }

        string text = RenderContents(containers[0]);
        CheckNormalized(text, source);
        return text;
    }

    private static void CheckNormalized(string text, string source)
    {
        if (text.Length == 0)
        {
            throw new CorpusExtractionException($"{source}: the content container yielded no text");
        }

        // Stricter than the prototype, which has no such check. A wrong or missing charset, or invalid
        // bytes, decode silently to U+FFFD; that text would be sent, cited and quote-checked with
        // characters missing. None of the corpus pages contains U+FFFD.
        int replacement = text.IndexOf('\uFFFD', StringComparison.Ordinal);
        if (replacement >= 0)
        {
            throw new CorpusExtractionException(
                $"{source}: the extracted text contains U+FFFD, the replacement character, at character " +
                $"{replacement}. Part of the page did not decode; check its charset (the meta tag or a " +
                $"byte-order mark).");
        }

        foreach ((char c, string name) in new[] { ('\r', "a carriage return"), ('\u00A0', "a U+00A0 no-break space") })
        {
            if (text.Contains(c))
            {
                throw new CorpusExtractionException(
                    $"{source}: extracted text still contains {name}; whitespace normalization is broken");
            }
        }
    }

    // Renders everything inside the element (not the text after it) as paragraphs.
    private static string RenderContents(INode element)
    {
        var output = new Paragraphs();
        WalkChildren(element, output);
        return output.Render();
    }

    // The DOM equivalent of the prototype's walk over lxml's text, children and tails: a text node
    // between two elements is lxml's tail of the first, and joins the parent's flow.
    private static void WalkChildren(INode parent, Paragraphs output)
    {
        foreach (INode child in parent.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    output.AddText(text.Data);
                    break;
                case IElement element:
                    Walk(element, output);
                    break;
                // Comments, processing instructions and doctypes contribute nothing, as in the
                // prototype, whose parser removes comments and processing instructions.
            }
        }
    }

    private static void Walk(IElement element, Paragraphs output)
    {
        string tag = element.LocalName;
        if (StrippedTags.Contains(tag))
        {
            return;
        }

        if (tag == "br")
        {
            output.LineBreak();
        }
        else if (tag == "table")
        {
            output.AddParagraph(RenderTable(element));
        }
        else if (BlockTags.Contains(tag))
        {
            output.EndParagraph();
            WalkChildren(element, output);
            output.EndParagraph();
        }
        else
        {
            WalkChildren(element, output);
        }
    }

    // Everything inside the element on one line, for a table cell.
    private static string Flatten(IElement element) => PythonWhitespace.Collapse(RenderContents(element));

    // One line per row. Empty cells are kept so later cells stay in their columns; rows with no text at
    // all are dropped. rowspan and colspan are not expanded. Only the table's own children are read,
    // as in the prototype: text directly inside a <table> is not part of any row.
    private static string RenderTable(IElement table)
    {
        List<string> lines = [];
        foreach (IElement child in table.Children)
        {
            switch (child.LocalName)
            {
                case "caption":
                    lines.Add(Flatten(child));
                    break;
                case "tr":
                    lines.Add(RenderRow(child));
                    break;
                case "thead" or "tbody" or "tfoot":
                    lines.AddRange(child.Children.Where(row => row.LocalName == "tr").Select(RenderRow));
                    break;
            }
        }

        return string.Join('\n', lines.Where(line => line.Length > 0));
    }

    private static string RenderRow(IElement row)
    {
        List<string> cells = row.Children
            .Where(cell => cell.LocalName is "td" or "th")
            .Select(Flatten)
            .ToList();

        return cells.All(cell => cell.Length == 0)
            ? ""
            : PythonWhitespace.TrimEnd(string.Join(CellSeparator, cells));
    }

    /// <summary>Accumulates text as paragraphs made of hard-broken lines.</summary>
    private sealed class Paragraphs
    {
        private readonly List<string> _paragraphs = [];
        private readonly List<string> _lines = [];
        private readonly StringBuilder _fragments = new();

        public void AddText(string? text) => _fragments.Append(text);

        public void LineBreak()
        {
            _lines.Add(PythonWhitespace.Collapse(_fragments.ToString()));
            _fragments.Clear();
        }

        public void EndParagraph()
        {
            LineBreak();
            List<string> lines = _lines.Where(line => line.Length > 0).ToList();
            if (lines.Count > 0)
            {
                _paragraphs.Add(string.Join('\n', lines));
            }

            _lines.Clear();
        }

        // Pre-rendered text, such as a table, as a paragraph of its own.
        public void AddParagraph(string text)
        {
            EndParagraph();
            if (text.Length > 0)
            {
                _paragraphs.Add(text);
            }
        }

        public string Render()
        {
            EndParagraph();
            return string.Join("\n\n", _paragraphs);
        }
    }
}
