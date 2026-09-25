using Bunit;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Text;
using DocQaMs.Web.Components.Viewer;

namespace DocQaMs.Web.Tests.Viewer;

/// <summary>Plan item 9: one paragraph of a document, its highlight, and when it renders again.</summary>
public sealed class DocumentParagraphTests : IDisposable
{
    private const string Text = "Line one\nline two\n\nBravo two\n\n<script>alert(1)</script>";

    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void AParagraph_IsRenderedAsText_KeepingItsLineBreaks()
    {
        IRenderedComponent<DocumentParagraph> cut = Render(0);

        Assert.Equal("Line one\nline two", cut.Find("p").TextContent);
        Assert.Equal("paragraph-0", cut.Find("p").Id);
        Assert.Empty(cut.FindAll("mark"));
    }

    [Fact]
    public void TheHighlight_MarksTheCitedPart_AndLeavesTheParagraphsTextWhole()
    {
        IRenderedComponent<DocumentParagraph> cut = Render(1, new TextRange(0, 5));

        Assert.Equal("Bravo", cut.Find("mark").TextContent);
        Assert.Equal("Bravo two", cut.Find("p").TextContent);
    }

    [Fact]
    public void AHighlightOverTheWholeParagraph_MarksAllOfIt()
    {
        IRenderedComponent<DocumentParagraph> cut = Render(1, new TextRange(0, 9));

        Assert.Equal("Bravo two", cut.Find("mark").TextContent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 4)]
    [InlineData(5, 3)]
    [InlineData(0, 100)]
    public void AHighlightThatDoesNotFitTheParagraph_IsIgnored(int start, int end)
    {
        IRenderedComponent<DocumentParagraph> cut = Render(1, new TextRange(start, end));

        Assert.Empty(cut.FindAll("mark"));
        Assert.Equal("Bravo two", cut.Find("p").TextContent);
    }

    [Fact]
    public void DocumentText_IsEncoded_Highlighted_OrNot()
    {
        IRenderedComponent<DocumentParagraph> cut = Render(2, new TextRange(0, 8));

        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("&lt;script&gt;", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("<script>alert(1)</script>", cut.Find("p").TextContent);
    }

    [Fact]
    public void ItRendersAgain_OnlyWhenItsOwnTextOrHighlightChanges()
    {
        IRenderedComponent<DocumentParagraph> cut = Render(1);
        Assert.Equal(1, cut.RenderCount);

        cut.Render(p => p.Add(x => x.Highlight, null));
        Assert.Equal(1, cut.RenderCount);

        cut.Render(p => p.Add(x => x.Highlight, new TextRange(0, 5)));
        Assert.Equal(2, cut.RenderCount);

        cut.Render(p => p.Add(x => x.Highlight, new TextRange(0, 5)));
        Assert.Equal(2, cut.RenderCount);

        cut.Render(p => p.Add(x => x.Index, 0));
        Assert.Equal(3, cut.RenderCount);
    }

    private IRenderedComponent<DocumentParagraph> Render(int index, TextRange? highlight = null)
    {
        CorpusDocument document = new("a.htm", new RequestDocument("Part A", "Source file: a.htm", Text), null);
        return _context.Render<DocumentParagraph>(p => p
            .Add(x => x.Document, document)
            .Add(x => x.Index, index)
            .Add(x => x.Highlight, highlight));
    }
}
