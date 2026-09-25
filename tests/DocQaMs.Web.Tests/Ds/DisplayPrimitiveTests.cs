using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using DocQaMs.Web.Components.Ds;

namespace DocQaMs.Web.Tests;

public sealed class DisplayPrimitiveTests : IDisposable
{
    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Theory]
    [InlineData(IconName.Feather)]
    [InlineData(IconName.BookOpen)]
    [InlineData(IconName.Settings)]
    [InlineData(IconName.X)]
    [InlineData(IconName.ChevronDown)]
    public void EveryIcon_IsADecorativeOutlineGlyph(IconName name)
    {
        IElement svg = _context.Render<Icon>(p => p.Add(x => x.Name, name)).Find("svg");

        Assert.Equal("d1-icon", svg.ClassName);
        Assert.Equal("true", svg.GetAttribute("aria-hidden"));
        Assert.Equal("false", svg.GetAttribute("focusable"));
        Assert.Equal("0 0 24 24", svg.GetAttribute("viewBox"));
        Assert.Equal("18", svg.GetAttribute("width"));
        Assert.Equal("currentColor", svg.GetAttribute("stroke"));
        Assert.NotEmpty(svg.Children);
    }

    [Fact]
    public void TheStrokeWidth_IsWrittenTheSameInEveryCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            IElement svg = _context.Render<Icon>(p => p.Add(x => x.Name, IconName.X)).Find("svg");

            Assert.Equal("1.5", svg.GetAttribute("stroke-width"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AnUnknownGlyph_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _context.Render<Icon>(p => p.Add(x => x.Name, (IconName)99)));

    [Theory]
    [InlineData(BadgeTone.Neutral, "d1-badge--neutral")]
    [InlineData(BadgeTone.Accent, "d1-badge--accent")]
    [InlineData(BadgeTone.Success, "d1-badge--success")]
    [InlineData(BadgeTone.Warning, "d1-badge--warning")]
    [InlineData(BadgeTone.Danger, "d1-badge--danger")]
    [InlineData(BadgeTone.Info, "d1-badge--info")]
    [InlineData(BadgeTone.Solid, "d1-badge--solid")]
    public void EachBadgeTone_UsesTheDesignSystemsClass(BadgeTone tone, string expected)
    {
        IElement badge = _context.Render<Badge>(p => p.Add(x => x.Tone, tone).AddChildContent("Matched")).Find(".d1-badge");

        Assert.Equal("d1-badge " + expected, badge.ClassName);
        Assert.Equal("Matched", badge.TextContent.Trim());
    }

    [Fact]
    public void ABadgesDot_IsDecorative()
    {
        IElement dot = _context.Render<Badge>(p => p.Add(x => x.Dot, true)).Find(".d1-badge__dot");

        Assert.Equal("true", dot.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void ACard_HasItsPartsInOrder()
    {
        IRenderedComponent<Card> cut = _context.Render<Card>(p => p
            .Add(x => x.Eyebrow, "Corpus")
            .Add(x => x.Title, "Corpus health")
            .AddChildContent("<p>Unchanged</p>")
            .Add(x => x.Footer, "<span>Reload</span>"));

        IElement card = cut.Find("div.d1-card");
        Assert.Equal("d1-card", card.ClassName);
        string[] parts = ["d1-card__eyebrow", "d1-card__title", "d1-card__body", "d1-card__footer"];
        Assert.Equal(parts, card.Children.Select(child => child.ClassName));
        Assert.Equal("H3", card.Children[1].TagName);
    }

    [Theory]
    [InlineData(CardVariant.Plain, "d1-card--plain")]
    [InlineData(CardVariant.Sunken, "d1-card--sunken")]
    [InlineData(CardVariant.Inverse, "d1-card--inverse")]
    [InlineData(CardVariant.Framed, "d1-card--framed")]
    public void EachCardVariant_UsesTheDesignSystemsClass(CardVariant variant, string expected) =>
        Assert.Contains(expected, _context.Render<Card>(p => p.Add(x => x.Variant, variant)).Find(".d1-card").ClassList);

    [Fact]
    public void AClickableCard_IsAButton_HoldingOnlyPhrasingContent()
    {
        int clicks = 0;
        IRenderedComponent<Card> cut = _context.Render<Card>(p => p
            .Add(x => x.Title, "A suggestion")
            .AddChildContent("What are the sump requirements?")
            .Add(x => x.OnClick, () => clicks++));

        IElement card = cut.Find("button.d1-card");
        Assert.Equal("button", card.GetAttribute("type"));
        Assert.Contains("d1-card--interactive", card.ClassList);
        Assert.All(card.Children, child => Assert.Equal("SPAN", child.TagName));

        card.Click();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void AClickableCard_CannotHaveAFooter() =>
        Assert.Throws<InvalidOperationException>(() => _context.Render<Card>(p => p
            .Add(x => x.OnClick, () => { })
            .Add(x => x.Footer, "<span>Reload</span>")));

    [Fact]
    public void APlainTag_IsNotAButton()
    {
        IElement tag = _context.Render<Tag>(p => p.AddChildContent("175.410")).Find(".d1-tag");

        Assert.Equal("SPAN", tag.TagName);
        Assert.Equal("d1-tag", tag.ClassName);
    }

    [Fact]
    public void AClickableTag_IsAButton()
    {
        int clicks = 0;
        IElement tag = _context.Render<Tag>(p => p.Add(x => x.OnClick, () => clicks++)).Find("button.d1-tag");

        Assert.Contains("d1-tag--interactive", tag.ClassList);
        tag.Click();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void ASelectedTag_SaysSo_NotOnlyByColor()
    {
        IElement tag = _context.Render<Tag>(p => p.Add(x => x.Selected, true)).Find(".d1-tag");

        Assert.Contains("d1-tag--selected", tag.ClassList);
        Assert.Equal("true", tag.GetAttribute("aria-current"));
    }

    [Fact]
    public void TheLiveRegion_IsPolite_Whole_AndHiddenVisually()
    {
        IElement region = _context.Render<LiveRegion>(p => p.Add(x => x.Message, "Answer stopped")).Find("div");

        Assert.Equal("status", region.GetAttribute("role"));
        Assert.Equal("polite", region.GetAttribute("aria-live"));
        Assert.Equal("true", region.GetAttribute("aria-atomic"));
        Assert.Equal("visually-hidden", region.ClassName);
        Assert.Equal("Answer stopped", region.TextContent);
    }

    [Fact]
    public void TheLiveRegion_IsPresentBeforeAnythingIsAnnounced() =>
        Assert.Equal("polite", _context.Render<LiveRegion>().Find("div").GetAttribute("aria-live"));
}
