using AngleSharp.Dom;
using Bunit;
using DocQaMs.Web.Components.Ds;

namespace DocQaMs.Web.Tests;

public sealed class FormPrimitiveTests : IDisposable
{
    private static readonly SelectOption[] Models =
    [
        new("claude-opus-5", "Claude Opus 5"),
        new("claude-sonnet-5", "Claude Sonnet 5", "Excluded: the request shape failed"),
    ];

    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void AnInputWithoutALabel_IsJustTheControl()
    {
        IRenderedComponent<TextInput> cut = _context.Render<TextInput>(p => p.AddUnmatched("placeholder", "Ask about the corpus"));

        IElement input = cut.Find("input");
        Assert.Equal("d1-input", input.ClassName);
        Assert.Equal("Ask about the corpus", input.GetAttribute("placeholder"));
        Assert.Empty(cut.FindAll(".d1-field"));
    }

    [Fact]
    public void AnInputsLabel_IsTiedToIt_EvenWithoutAGivenId()
    {
        IRenderedComponent<TextInput> cut = _context.Render<TextInput>(p => p.Add(x => x.Label, "Question"));

        string? id = cut.Find("input").Id;
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(id, cut.Find("label.d1-field__label").GetAttribute("for"));
    }

    [Fact]
    public void AnInputsHint_DescribesIt()
    {
        IRenderedComponent<TextInput> cut = _context.Render<TextInput>(p => p
            .Add(x => x.Id, "question")
            .Add(x => x.Hint, "One question at a time."));

        Assert.Equal("question-hint", cut.Find(".d1-field__hint").Id);
        Assert.Equal("question-hint", cut.Find("input").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void AnError_ReplacesTheHint_AndMarksTheInputInvalid()
    {
        IRenderedComponent<TextInput> cut = _context.Render<TextInput>(p => p
            .Add(x => x.Id, "question")
            .Add(x => x.Hint, "One question at a time.")
            .Add(x => x.Error, "Must not be empty."));

        IElement input = cut.Find("input");
        Assert.Equal("true", input.GetAttribute("aria-invalid"));
        Assert.Equal("question-error", input.GetAttribute("aria-describedby"));
        Assert.Equal("Must not be empty.", cut.Find(".d1-field__error").TextContent);
        Assert.Empty(cut.FindAll(".d1-field__hint"));
    }

    [Fact]
    public void ACallersDescription_IsKept_BesideTheHint()
    {
        IRenderedComponent<TextInput> cut = _context.Render<TextInput>(p => p
            .Add(x => x.Id, "question")
            .Add(x => x.Hint, "One question at a time.")
            .AddUnmatched("aria-describedby", "corpus-status"));

        Assert.Equal("corpus-status question-hint", cut.Find("input").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void AMultilineInput_IsATextarea()
    {
        IElement area = _context.Render<TextInput>(p => p
            .Add(x => x.Multiline, true)
            .Add(x => x.Size, ControlSize.Lg)).Find("textarea");

        Assert.Equal("d1-input d1-input--area", area.ClassName);
    }

    [Theory]
    [InlineData(ControlSize.Sm, "d1-input d1-input--sm")]
    [InlineData(ControlSize.Md, "d1-input")]
    [InlineData(ControlSize.Lg, "d1-input d1-input--lg")]
    public void EachInputSize_UsesTheDesignSystemsClass(ControlSize size, string expected) =>
        Assert.Equal(expected, _context.Render<TextInput>(p => p.Add(x => x.Size, size)).Find("input").ClassName);

    [Fact]
    public void Typing_ReportsTheText()
    {
        string? value = null;
        IRenderedComponent<TextInput> cut = _context.Render<TextInput>(p => p
            .Add(x => x.Multiline, true)
            .Add(x => x.ValueChanged, (string text) => value = text));

        cut.Find("textarea").Input("What are the sump requirements?");

        Assert.Equal("What are the sump requirements?", value);
    }

    [Fact]
    public void ASelect_IsANativeSelect_WithTheDesignSystemsCaret()
    {
        IRenderedComponent<Select> cut = _context.Render<Select>(p => p.Add(x => x.Options, Models));

        Assert.Equal("d1-input d1-select__native", cut.Find("span.d1-select > select").ClassName);
        IElement caret = cut.Find("span.d1-select > svg");
        Assert.Contains("d1-select__caret", caret.ClassList);
        Assert.Equal("true", caret.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void ASelectsLabel_IsVisible_AndTiedToIt()
    {
        IRenderedComponent<Select> cut = _context.Render<Select>(p => p
            .Add(x => x.Label, "Model")
            .Add(x => x.Options, Models));

        IElement label = cut.Find("label.d1-field__label");
        Assert.Equal("Model", label.TextContent);
        Assert.Equal(cut.Find("select").Id, label.GetAttribute("for"));
    }

    [Fact]
    public void TheChosenOption_IsSelected()
    {
        IRenderedComponent<Select> cut = _context.Render<Select>(p => p
            .Add(x => x.Options, Models)
            .Add(x => x.Value, "claude-opus-5"));

        IElement[] options = [.. cut.FindAll("option")];
        Assert.Equal(["claude-opus-5", "claude-sonnet-5"], options.Select(option => option.GetAttribute("value")));
        Assert.True(options[0].HasAttribute("selected"));
        Assert.False(options[1].HasAttribute("selected"));
    }

    [Fact]
    public void ADisabledOption_SaysWhy()
    {
        IElement option = _context.Render<Select>(p => p.Add(x => x.Options, Models)).FindAll("option")[1];

        Assert.True(option.HasAttribute("disabled"));
        Assert.Equal("Claude Sonnet 5 — Excluded: the request shape failed", option.TextContent);
    }

    [Fact]
    public void ASelect_PointsAtTheCostPreview()
    {
        IElement select = _context.Render<Select>(p => p
            .Add(x => x.Options, Models)
            .AddUnmatched("aria-describedby", "cost-preview")).Find("select");

        Assert.Equal("cost-preview", select.GetAttribute("aria-describedby"));
    }

    [Fact]
    public void Choosing_ReportsTheValue()
    {
        string? value = null;
        IRenderedComponent<Select> cut = _context.Render<Select>(p => p
            .Add(x => x.Options, Models)
            .Add(x => x.ValueChanged, (string chosen) => value = chosen));

        cut.Find("select").Change("claude-sonnet-5");

        Assert.Equal("claude-sonnet-5", value);
    }
}
