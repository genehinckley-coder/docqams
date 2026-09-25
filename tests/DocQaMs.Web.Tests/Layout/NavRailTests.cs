using AngleSharp.Dom;
using Bunit;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Conversations;
using DocQaMs.Web.Components.Layout;
using DocQaMs.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DocQaMs.Web.Tests;

public sealed class NavRailTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly FakeConversationStore _store = new();

    public NavRailTests() => _context.Services.AddSingleton<IConversationStore>(_store);

    public void Dispose() => _context.Dispose();

    [Fact]
    public void TheRail_LinksTheThreeScreens()
    {
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        IReadOnlyList<IElement> links = cut.FindAll("nav a");
        string[] labels = ["Conversation", "Documents", "Corpus settings"];
        string[] hrefs = ["", "documents", "settings"];
        Assert.Equal(labels, links.Select(link => link.TextContent.Trim()));
        Assert.Equal(hrefs, links.Select(link => link.GetAttribute("href")));
        Assert.Equal("Main", cut.Find("nav").GetAttribute("aria-label"));
        Assert.All(cut.FindAll("svg"), svg => Assert.Equal("true", svg.GetAttribute("aria-hidden")));
    }

    [Fact]
    public void TheCurrentScreen_IsMarked_ByMoreThanColor()
    {
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        _context.Services.GetRequiredService<NavigationManager>().NavigateTo("documents");

        cut.WaitForAssertion(() =>
        {
            IElement active = Assert.Single(cut.FindAll("a.active"));
            Assert.Equal("Documents", active.TextContent.Trim());
            Assert.Equal("page", active.GetAttribute("aria-current"));
        });
    }

    [Fact]
    public void TheLinks_CarryTheOpenConversation_SoTheOtherScreensShowIt()
    {
        Guid id = Guid.NewGuid();
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        _context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/?c={id}");

        cut.WaitForAssertion(() => Assert.Equal(
            [$"?c={id}", $"documents?c={id}", $"settings?c={id}"],
            cut.FindAll("nav a").Select(link => link.GetAttribute("href"))));
    }

    [Fact]
    public void TheCurrentScreen_StaysMarked_WhenTheAddressCarriesAQuery()
    {
        Guid id = Guid.NewGuid();
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        _context.Services.GetRequiredService<NavigationManager>().NavigateTo($"documents?c={id}&doc=1&answer=0&source=1");

        cut.WaitForAssertion(() => Assert.Equal("Documents", Assert.Single(cut.FindAll("a.active")).TextContent.Trim()));
    }

    [Fact]
    public void Disposing_StopsFollowingTheAddress()
    {
        Guid id = Guid.NewGuid();
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        cut.Instance.Dispose();
        _context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/?c={id}");

        Assert.DoesNotContain(id.ToString(), cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTotalCost_StartsAtZero()
    {
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        Assert.Equal("$0.00", cut.Find(".rail__amount").TextContent);
        Assert.Empty(cut.FindAll(".rail__note"));
    }

    [Fact]
    public void TheTotalCost_FollowsTheStore()
    {
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        _store.PublishTotal(new ConversationCost(new TurnCost(0m, 2.00m, 0m, 0.13m, 0.03m), 0, 0));

        cut.WaitForAssertion(() => Assert.Equal("$2.16", cut.Find(".rail__amount").TextContent));
    }

    [Fact]
    public void TurnsOfUnknownOrPartialCost_AreStated()
    {
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();

        _store.PublishTotal(new ConversationCost(new TurnCost(0.10m, 0m, 0m, 0m, 0m), 1, 2));

        cut.WaitForAssertion(() =>
        {
            string[] notes = ["Excludes 1 turn of unknown cost", "Output not reported for 2 stopped or failed turns"];
            Assert.Equal(notes, cut.FindAll(".rail__note").Select(note => note.TextContent.Trim()));
        });
    }

    [Fact]
    public void Disposing_Unsubscribes()
    {
        IRenderedComponent<NavRail> cut = _context.Render<NavRail>();
        Assert.Equal(1, _store.SubscriberCount);

        cut.Instance.Dispose();

        Assert.Equal(0, _store.SubscriberCount);
    }
}
