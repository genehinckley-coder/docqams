using AngleSharp.Dom;
using Bunit;
using DocQaMs.Web.Components.Ds;
using Microsoft.AspNetCore.Components.Web;

namespace DocQaMs.Web.Tests;

public sealed class TooltipTests : IDisposable
{
    private const string Visible = "d1-tooltip__bubble--visible";

    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void TheTrigger_IsFocusable_AndDescribedByTheBubble()
    {
        IRenderedComponent<Tooltip> cut = Render();

        IElement trigger = cut.Find(".d1-tooltip");
        IElement bubble = Bubble(cut);
        Assert.Equal("0", trigger.GetAttribute("tabindex"));
        Assert.Equal("tooltip", bubble.GetAttribute("role"));
        Assert.Equal(bubble.Id, trigger.GetAttribute("aria-describedby"));
        Assert.Equal("Provenance, not entailment", bubble.TextContent);
        Assert.DoesNotContain(Visible, bubble.ClassList);
    }

    [Fact]
    public async Task Hover_ShowsIt_AndLeavingHidesIt()
    {
        IRenderedComponent<Tooltip> cut = Render();

        await cut.Find(".d1-tooltip").TriggerEventAsync("onmouseenter", new MouseEventArgs());
        Assert.Contains(Visible, Bubble(cut).ClassList);

        await cut.Find(".d1-tooltip").TriggerEventAsync("onmouseleave", new MouseEventArgs());
        Assert.DoesNotContain(Visible, Bubble(cut).ClassList);
    }

    [Fact]
    public void KeyboardFocus_ShowsIt_AndLosingFocusHidesIt()
    {
        IRenderedComponent<Tooltip> cut = Render();

        cut.Find(".d1-tooltip").FocusIn();
        Assert.Contains(Visible, Bubble(cut).ClassList);

        cut.Find(".d1-tooltip").FocusOut();
        Assert.DoesNotContain(Visible, Bubble(cut).ClassList);
    }

    [Fact]
    public void Escape_DismissesIt_WithoutMovingFocus()
    {
        IRenderedComponent<Tooltip> cut = Render();
        cut.Find(".d1-tooltip").FocusIn();

        cut.Find(".d1-tooltip").KeyDown(new KeyboardEventArgs { Key = "a" });
        Assert.Contains(Visible, Bubble(cut).ClassList);

        cut.Find(".d1-tooltip").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.DoesNotContain(Visible, Bubble(cut).ClassList);
    }

    [Theory]
    [InlineData(TooltipPlacement.Top, "d1-tooltip__bubble--top")]
    [InlineData(TooltipPlacement.Bottom, "d1-tooltip__bubble--bottom")]
    [InlineData(TooltipPlacement.Left, "d1-tooltip__bubble--left")]
    [InlineData(TooltipPlacement.Right, "d1-tooltip__bubble--right")]
    public void EachPlacement_UsesTheDesignSystemsClass(TooltipPlacement placement, string expected) =>
        Assert.Contains(expected, Bubble(Render(placement)).ClassList);

    private IRenderedComponent<Tooltip> Render(TooltipPlacement placement = TooltipPlacement.Top) =>
        _context.Render<Tooltip>(p => p
            .Add(x => x.Label, "Provenance, not entailment")
            .Add(x => x.Placement, placement)
            .AddChildContent("<span>Sources</span>"));

    private static IElement Bubble(IRenderedComponent<Tooltip> cut) => cut.Find(".d1-tooltip__bubble");
}
