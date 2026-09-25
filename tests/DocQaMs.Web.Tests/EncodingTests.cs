using Bunit;
using DocQaMs.Web.Components.Ds;

namespace DocQaMs.Web.Tests;

/// <summary>
/// Model output, cited text and exception messages reach these primitives as text. Blazor encodes text, and no
/// primitive turns it into markup, so a payload that looks like HTML is shown as written, never run.
/// </summary>
public sealed class EncodingTests : IDisposable
{
    private const string Payload = "<script>alert(1)</script>";

    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void BadgeText_IsEncoded() =>
        AssertEncoded(_context.Render<Badge>(p => p.AddChildContent(builder => builder.AddContent(0, Payload))));

    [Fact]
    public void CardText_IsEncoded() =>
        AssertEncoded(_context.Render<Card>(p => p
            .Add(x => x.Eyebrow, Payload)
            .Add(x => x.Title, Payload)
            .AddChildContent(builder => builder.AddContent(0, Payload))));

    [Fact]
    public void TooltipText_IsEncoded() =>
        AssertEncoded(_context.Render<Tooltip>(p => p.Add(x => x.Label, Payload)));

    [Fact]
    public void FieldText_IsEncoded() =>
        AssertEncoded(_context.Render<TextInput>(p => p
            .Add(x => x.Label, Payload)
            .Add(x => x.Error, Payload)
            .Add(x => x.Value, Payload)));

    [Fact]
    public void OptionText_IsEncoded() =>
        AssertEncoded(_context.Render<Select>(p => p.Add(x => x.Options, [new SelectOption("v", Payload, Payload)])));

    [Fact]
    public void Announcements_AreEncoded() =>
        AssertEncoded(_context.Render<LiveRegion>(p => p.Add(x => x.Message, Payload)));

    private static void AssertEncoded<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("&lt;script&gt;", cut.Markup, StringComparison.Ordinal);
    }
}
