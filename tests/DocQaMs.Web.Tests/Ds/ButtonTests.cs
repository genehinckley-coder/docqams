using AngleSharp.Dom;
using Bunit;
using DocQaMs.Web.Components.Ds;

namespace DocQaMs.Web.Tests;

public sealed class ButtonTests : IDisposable
{
    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void TheDefaults_AreAMediumPrimaryButton()
    {
        IElement button = _context.Render<Button>(p => p.AddChildContent("Ask")).Find("button");

        Assert.Equal("d1-btn d1-btn--primary d1-btn--md", button.ClassName);
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Equal("Ask", button.TextContent.Trim());
        Assert.False(button.HasAttribute("disabled"));
        Assert.False(button.HasAttribute("aria-disabled"));
    }

    [Theory]
    [InlineData(ButtonVariant.Primary, "d1-btn--primary")]
    [InlineData(ButtonVariant.Secondary, "d1-btn--secondary")]
    [InlineData(ButtonVariant.Ghost, "d1-btn--ghost")]
    [InlineData(ButtonVariant.Danger, "d1-btn--danger")]
    [InlineData(ButtonVariant.Inverse, "d1-btn--inverse")]
    public void EachVariant_UsesTheDesignSystemsClass(ButtonVariant variant, string expected) =>
        Assert.Contains(expected, _context.Render<Button>(p => p.Add(x => x.Variant, variant)).Find("button").ClassList);

    [Theory]
    [InlineData(ControlSize.Sm, "d1-btn--sm")]
    [InlineData(ControlSize.Md, "d1-btn--md")]
    [InlineData(ControlSize.Lg, "d1-btn--lg")]
    public void EachSize_UsesTheDesignSystemsClass(ControlSize size, string expected) =>
        Assert.Contains(expected, _context.Render<Button>(p => p.Add(x => x.Size, size)).Find("button").ClassList);

    [Fact]
    public void FullWidth_AddsTheBlockClass() =>
        Assert.Contains("d1-btn--block", _context.Render<Button>(p => p.Add(x => x.FullWidth, true)).Find("button").ClassList);

    [Fact]
    public void Icons_SitBeforeAndAfterTheLabel()
    {
        IElement button = _context.Render<Button>(p => p
            .Add(x => x.IconLeft, IconName.X)
            .Add(x => x.IconRight, IconName.ChevronDown)
            .AddChildContent("Stop")).Find("button");

        IElement[] parts = [.. button.Children];
        Assert.Equal(3, parts.Length);
        Assert.Equal("d1-btn__icon", parts[0].ClassName);
        Assert.NotNull(parts[0].QuerySelector("svg.d1-icon"));
        Assert.Equal("Stop", parts[1].TextContent);
        Assert.Equal("d1-btn__icon", parts[2].ClassName);
    }

    [Fact]
    public void AClick_CallsOnClick()
    {
        int clicks = 0;
        IRenderedComponent<Button> cut = _context.Render<Button>(p => p.Add(x => x.OnClick, () => clicks++));

        cut.Find("button").Click();

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void ADisabledButton_IsNativelyDisabled_AndIgnoresClicks()
    {
        int clicks = 0;
        IRenderedComponent<Button> cut = _context.Render<Button>(p => p
            .Add(x => x.Disabled, true)
            .Add(x => x.OnClick, () => clicks++));

        IElement button = cut.Find("button");
        Assert.True(button.HasAttribute("disabled"));
        button.Click();
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void AnAriaDisabledButton_KeepsFocus_AndIgnoresClicks()
    {
        int clicks = 0;
        IRenderedComponent<Button> cut = _context.Render<Button>(p => p
            .Add(x => x.AriaDisabled, true)
            .Add(x => x.OnClick, () => clicks++));

        IElement button = cut.Find("button");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));
        button.Click();
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void ClassesAndOtherAttributes_PassThrough()
    {
        IElement button = _context.Render<Button>(p => p
            .Add(x => x.Class, "ask")
            .AddUnmatched("aria-describedby", "reason")
            .AddUnmatched("title", "Send the question")).Find("button");

        Assert.Contains("ask", button.ClassList);
        Assert.Contains("d1-btn", button.ClassList);
        Assert.Equal("reason", button.GetAttribute("aria-describedby"));
        Assert.Equal("Send the question", button.GetAttribute("title"));
    }
}
