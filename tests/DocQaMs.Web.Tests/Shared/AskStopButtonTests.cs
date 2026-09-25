using AngleSharp.Dom;
using Bunit;
using DocQaMs.Core.Conversations;
using DocQaMs.Web.Components.Ds;
using DocQaMs.Web.Components.Shared;
using DocQaMs.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using static DocQaMs.Web.Tests.TestSupport.FakeConversationStore;

namespace DocQaMs.Web.Tests;

public sealed class AskStopButtonTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly FakeConversationStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    public AskStopButtonTests() => _context.Services.AddSingleton<IConversationStore>(_store);

    public void Dispose() => _context.Dispose();

    [Fact]
    public void AnIdleConversation_ShowsAsk_AndAskCallsThePage()
    {
        _store.Publish(Idle(_id));
        int asks = 0;
        IRenderedComponent<AskStopButton> cut = Render(onAsk: () => asks++);

        IElement button = cut.Find("button");
        Assert.Equal("Ask", Label(cut));
        Assert.Contains("d1-btn--primary", button.ClassList);
        Assert.False(button.HasAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));

        button.Click();
        Assert.Equal(1, asks);
        Assert.Empty(_store.StopCalls);
    }

    [Fact]
    public void AnUnavailableAsk_StaysFocusable_AndIgnoresClicks()
    {
        _store.Publish(Idle(_id));
        int asks = 0;
        IRenderedComponent<AskStopButton> cut = Render(canAsk: false, onAsk: () => asks++);

        IElement button = cut.Find("button");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));

        button.Click();
        Assert.Equal(0, asks);
    }

    [Fact]
    public void WithNoConversationYet_ItShowsAsk()
    {
        IRenderedComponent<AskStopButton> cut = _context.Render<AskStopButton>(p => p.Add(x => x.CanAsk, true));

        Assert.Equal("Ask", Label(cut));
    }

    [Fact]
    public void AnAnswerInFlight_ShowsStop_EvenInATabOpenedMidAnswer()
    {
        _store.Publish(Answering(_id));
        IRenderedComponent<AskStopButton> cut = Render();

        IElement button = cut.Find("button");
        Assert.Equal("Stop", Label(cut));
        Assert.Contains("d1-btn--secondary", button.ClassList);
        Assert.NotNull(button.QuerySelector("svg.d1-icon"));
        Assert.False(button.HasAttribute("aria-disabled"));
    }

    [Fact]
    public void Stop_CallsTheStore_ThenShowsStopping_AndIgnoresASecondClick()
    {
        _store.Publish(Answering(_id));
        IRenderedComponent<AskStopButton> cut = Render();

        cut.Find("button").Click();

        Assert.Equal([_id], _store.StopCalls);
        cut.WaitForAssertion(() => Assert.Equal("Stopping…", Label(cut)));
        IElement button = cut.Find("button");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));

        button.Click();
        Assert.Single(_store.StopCalls);
    }

    [Fact]
    public void AStopClickedInAnotherTab_ShowsStoppingHere()
    {
        _store.Publish(Answering(_id));
        IRenderedComponent<AskStopButton> cut = Render();

        _store.Publish(Answering(_id, stopping: true));

        cut.WaitForAssertion(() => Assert.Equal("Stopping…", Label(cut)));
    }

    [Fact]
    public void ThePublishedOutcome_ResolvesStoppingBackToAsk()
    {
        _store.Publish(Answering(_id, stopping: true));
        IRenderedComponent<AskStopButton> cut = Render();

        _store.Publish(Idle(_id));

        cut.WaitForAssertion(() => Assert.Equal("Ask", Label(cut)));
    }

    [Fact]
    public void ItIsTheSameButtonElement_BeforeDuringAndAfterAnAnswer()
    {
        // The Button component renders exactly one <button>, always at the same place. The same Button instance
        // throughout therefore means the same element, so keyboard focus is never dropped.
        _store.Publish(Idle(_id));
        IRenderedComponent<AskStopButton> cut = Render();
        Button first = cut.FindComponent<Button>().Instance;

        _store.Publish(Answering(_id));
        cut.WaitForAssertion(() => Assert.Equal("Stop", Label(cut)));
        AssertOneButton(first, cut);

        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Equal("Stopping…", Label(cut)));
        AssertOneButton(first, cut);

        _store.Publish(Idle(_id));
        cut.WaitForAssertion(() => Assert.Equal("Ask", Label(cut)));
        AssertOneButton(first, cut);
    }

    [Fact]
    public void AStopClickedAsTheAnswerFinishes_NeverBecomesAnAsk()
    {
        _store.Publish(Answering(_id));
        int asks = 0;
        IRenderedComponent<AskStopButton> cut = Render(onAsk: () => asks++);

        // The answer finished, but its change has not reached this tab: the button still shows Stop.
        _store.Replace(Idle(_id));
        cut.Find("button").Click();

        Assert.Equal(0, asks);
        Assert.Equal([_id], _store.StopCalls);
        cut.WaitForAssertion(() => Assert.Equal("Ask", Label(cut)));
    }

    [Fact]
    public void AChangeToAnotherConversation_IsIgnored()
    {
        _store.Publish(Idle(_id));
        IRenderedComponent<AskStopButton> cut = Render();

        _store.Publish(Answering(Guid.NewGuid()));

        Assert.Equal("Ask", Label(cut));
    }

    [Fact]
    public void ExtraAttributes_ReachTheButton()
    {
        _store.Publish(Idle(_id));
        IRenderedComponent<AskStopButton> cut = _context.Render<AskStopButton>(p => p
            .Add(x => x.ConversationId, (Guid?)_id)
            .AddUnmatched("aria-describedby", "ask-unavailable-reason"));

        Assert.Equal("ask-unavailable-reason", cut.Find("button").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void Disposing_Unsubscribes()
    {
        IRenderedComponent<AskStopButton> cut = Render();
        Assert.Equal(1, _store.SubscriberCount);

        cut.Instance.Dispose();

        Assert.Equal(0, _store.SubscriberCount);
    }

    [Fact]
    public async Task Ask_IsIgnored_WhileThePagesAskIsStillRunning()
    {
        _store.Publish(Idle(_id));
        int asks = 0;
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IRenderedComponent<AskStopButton> cut = _context.Render<AskStopButton>(p => p
            .Add(x => x.ConversationId, (Guid?)_id)
            .Add(x => x.CanAsk, true)
            .Add(x => x.OnAsk, async () =>
            {
                asks++;
                await pending.Task;
            }));

        // Neither click is awaited until the page's Ask is released, so a missing latch fails here instead of
        // leaving the second click waiting forever.
        Task first = cut.Find("button").ClickAsync(new MouseEventArgs());
        Task second = cut.Find("button").ClickAsync(new MouseEventArgs());
        try
        {
            Assert.Equal(1, asks);
        }
        finally
        {
            pending.TrySetResult();
            await Task.WhenAll(first, second);
        }

        await cut.Find("button").ClickAsync(new MouseEventArgs());
        Assert.Equal(2, asks);
    }

    [Fact]
    public void SwitchingConversation_ShowsTheNewOnesState_WithoutAnnouncing()
    {
        Guid other = Guid.NewGuid();
        _store.Publish(Answering(_id));
        _store.Publish(Finished(other, TurnOutcome.Completed));
        IRenderedComponent<AskStopButton> cut = Render();
        Assert.Equal("Stop", Label(cut));

        cut.Render(p => p.Add(x => x.ConversationId, (Guid?)other));

        Assert.Equal("Ask", Label(cut));
        Assert.Equal("", Announcement(cut));

        // Changes to the conversation no longer shown are ignored; the shown one's are followed.
        _store.Publish(Answering(_id, stopping: true));
        Assert.Equal("Ask", Label(cut));
        _store.Publish(Answering(other));
        cut.WaitForAssertion(() => Assert.Equal("Stop", Label(cut)));
    }

    [Theory]
    [InlineData(TurnOutcome.Completed, "Answer complete")]
    [InlineData(TurnOutcome.Stopped, "Answer stopped")]
    [InlineData(TurnOutcome.Failed, "Answer failed")]
    [InlineData(TurnOutcome.NotReplayable, "Answer not added to the conversation")]
    public void AnAnswersEnd_IsAnnounced(TurnOutcome outcome, string expected)
    {
        _store.Publish(Answering(_id));
        IRenderedComponent<AskStopButton> cut = Render();
        Assert.Equal("", Announcement(cut));

        _store.Publish(Finished(_id, outcome));

        cut.WaitForAssertion(() => Assert.Equal(expected, Announcement(cut)));
    }

    [Fact]
    public void AnOutcomeAlreadyPublished_IsNotAnnouncedAgain_OnAFreshRender()
    {
        _store.Publish(Finished(_id, TurnOutcome.Stopped));

        IRenderedComponent<AskStopButton> cut = Render();

        Assert.Equal("", Announcement(cut));
    }

    [Fact]
    public void TheSameOutcomeTwiceInARow_ChangesTheRegionEachTime_SoBothAreRead()
    {
        _store.Publish(Answering(_id));
        IRenderedComponent<AskStopButton> cut = Render();
        _store.Publish(Finished(_id, TurnOutcome.Completed));
        cut.WaitForAssertion(() => Assert.Equal("Answer complete", Announcement(cut)));
        string first = Announcement(cut);

        _store.Publish(Answering(_id));
        cut.WaitForAssertion(() => Assert.Equal("Stop", Label(cut)));
        _store.Publish(Finished(_id, TurnOutcome.Completed));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEqual(first, Announcement(cut));
            Assert.Equal("Answer complete", Announcement(cut).TrimEnd((char)0xA0));
        });
    }

    private IRenderedComponent<AskStopButton> Render(bool canAsk = true, Action? onAsk = null) =>
        _context.Render<AskStopButton>(p => p
            .Add(x => x.ConversationId, (Guid?)_id)
            .Add(x => x.CanAsk, canAsk)
            .Add(x => x.OnAsk, onAsk ?? (() => { })));

    private static string Label(IRenderedComponent<AskStopButton> cut) => cut.Find("button").TextContent.Trim();

    private static string Announcement(IRenderedComponent<AskStopButton> cut) => cut.Find("[role=status]").TextContent;

    private static void AssertOneButton(Button first, IRenderedComponent<AskStopButton> cut)
    {
        Assert.Same(first, cut.FindComponent<Button>().Instance);
        IElement button = Assert.Single(cut.FindAll("button"));
        Assert.False(button.HasAttribute("disabled"));
    }
}
