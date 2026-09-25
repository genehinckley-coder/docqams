using AngleSharp.Dom;
using Bunit;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Web.Components.Conversations;
using DocQaMs.Web.Tests.TestSupport;

namespace DocQaMs.Web.Tests.Conversations;

public sealed class SettingsBarTests : IDisposable
{
    private static readonly CostPreviewText Preview = new("Opus 5, High: cold.", "Up to $2.80.", null, null);

    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void WhileEditable_TwoLabeledSelects_PointAtThePreview()
    {
        IRenderedComponent<SettingsBar> cut = _context.Render<SettingsBar>(p => p
            .Add(x => x.Settings, AnswerSettings.Default)
            .Add(x => x.Preview, Preview));

        IReadOnlyList<IElement> selects = cut.FindAll("select");
        Assert.Equal(2, selects.Count);
        Assert.Equal(["Model", "Level"], cut.FindAll("label").Select(label => label.TextContent));
        string previewId = cut.Find(".settings__preview").Id!;
        Assert.All(selects, select => Assert.Contains(previewId, select.GetAttribute("aria-describedby")!, StringComparison.Ordinal));

        Assert.Equal(["Claude Opus 5", "Claude Opus 5.5", "Claude Sonnet 5"], selects[0].QuerySelectorAll("option").Select(option => option.TextContent));
        Assert.Equal(["Low", "Medium", "High", "Extra high", "Max"], selects[1].QuerySelectorAll("option").Select(option => option.TextContent));
        Assert.Equal("High", selects[1].QuerySelector("option[selected]")!.TextContent);
    }

    [Fact]
    public void ChoosingAModelOrLevel_ReportsTheNewSettings()
    {
        List<AnswerSettings> changes = [];
        IRenderedComponent<SettingsBar> cut = _context.Render<SettingsBar>(p => p
            .Add(x => x.Settings, AnswerSettings.Default)
            .Add(x => x.OnChange, changes.Add));

        cut.FindAll("select")[0].Change(ModelIds.ClaudeSonnet5);
        cut.FindAll("select")[1].Change(nameof(EffortLevel.XHigh));

        Assert.Equal([TestData.Sonnet(EffortLevel.High), AnswerSettings.Create(ModelIds.ClaudeOpus55, EffortLevel.XHigh)], changes);
    }

    [Fact]
    public void AnExcludedCombination_IsDisabled_WithItsReason()
    {
        IRenderedComponent<SettingsBar> cut = _context.Render<SettingsBar>(p => p
            .Add(x => x.Settings, AnswerSettings.Default)
            .Add(x => x.Excluded, [new ExcludedCombination(ModelIds.ClaudeSonnet5, EffortLevel.High, "Rejected by the spike")]));

        IElement sonnet = cut.FindAll("select")[0].QuerySelectorAll("option")[2];
        Assert.True(sonnet.HasAttribute("disabled"));
        Assert.Contains("Rejected by the spike", sonnet.TextContent, StringComparison.Ordinal);
        Assert.All(cut.FindAll("select")[1].QuerySelectorAll("option"), option => Assert.False(option.HasAttribute("disabled")));
    }

    [Fact]
    public void WhileLocked_TheSettingsAreABadge_BesideNewConversationWithDifferentSettings()
    {
        int clicks = 0;
        IRenderedComponent<SettingsBar> cut = _context.Render<SettingsBar>(p => p
            .Add(x => x.Settings, TestData.Sonnet(EffortLevel.XHigh))
            .Add(x => x.Locked, true)
            .Add(x => x.Preview, Preview)
            .Add(x => x.OnNewWithDifferentSettings, () => clicks++));

        Assert.Empty(cut.FindAll("select"));
        Assert.Equal("SONNET 5 · EXTRA HIGH", cut.Find(".d1-badge").TextContent.Trim());
        IElement button = cut.Find("button");
        Assert.Equal("New conversation with different settings", button.TextContent.Trim());

        button.Click();
        Assert.Equal(1, clicks);

        // The preview stays visible while locked.
        Assert.Contains("Up to $2.80.", cut.Find(".settings__preview").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoCorpusToPrice_ThePreviewSaysSo() =>
        Assert.Equal(
            "The cost preview appears once the corpus has loaded.",
            _context.Render<SettingsBar>(p => p.Add(x => x.Settings, AnswerSettings.Default)).Find(".settings__preview").TextContent.Trim());

    [Fact]
    public void AColdWriteWarning_AndTheCaution_AreShown()
    {
        IRenderedComponent<SettingsBar> cut = _context.Render<SettingsBar>(p => p
            .Add(x => x.Settings, TestData.Sonnet(EffortLevel.Low))
            .Add(x => x.Preview, new CostPreviewText("Summary.", "Worst.", CostPreview.LowLevelCaution, "A cold write is in progress.")));

        Assert.Equal("A cold write is in progress.", cut.Find(".settings__warning").TextContent);
        Assert.Equal(CostPreview.LowLevelCaution, cut.Find(".settings__caution").TextContent);
    }
}
