using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Tests.Answering;

public sealed class AnswerSettingsTests
{
    [Fact]
    public void TheDefault_IsOpus55AtHigh()
    {
        Assert.Equal(ModelIds.ClaudeOpus55, AnswerSettings.Default.ModelId);
        Assert.Equal(EffortLevel.High, AnswerSettings.Default.Effort);
        Assert.Equal("Claude Opus 5.5, High", AnswerSettings.Default.ToString());
    }

    [Fact]
    public void EveryOfferedCombination_IsAccepted()
    {
        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            foreach (EffortLevel level in CacheAffecting.EffortLevels)
            {
                AnswerSettings settings = AnswerSettings.Create(model.Id, level);
                Assert.Equal(model, settings.Model);
                Assert.Equal(level, settings.Effort);
            }
        }
    }

    [Fact]
    public void SettingsWithTheSameModelAndLevel_AreEqual()
    {
        Assert.Equal(AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Low), AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Low));
        Assert.NotEqual(AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Low), AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.Low));
        Assert.NotEqual(AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.Low), AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.High));
    }

    [Theory]
    [InlineData("claude-haiku-4-5")]
    [InlineData("Claude-Opus-5")]
    [InlineData("claude-opus-5 ")]
    public void AModelOutsideTheSelectableSet_IsRefused(string modelId)
    {
        var ex = Assert.Throws<ArgumentException>(() => AnswerSettings.Create(modelId, EffortLevel.High));
        Assert.Contains("not a selectable model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyModel_IsRefused() =>
        Assert.Throws<ArgumentException>(() => AnswerSettings.Create("", EffortLevel.High));

    [Fact]
    public void ALevelOutsideTheSelectableSet_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AnswerSettings.Create(ModelIds.ClaudeOpus5, (EffortLevel)99));

    [Fact]
    public void AnExcludedCombination_IsRefusedWithItsReason_AndTheOthersStayAvailable()
    {
        ExcludedCombination[] excluded = [new(ModelIds.ClaudeSonnet5, EffortLevel.Max, "Rejected by the spike.")];

        var ex = Assert.Throws<ArgumentException>(() => AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.Max, excluded));
        Assert.Contains("Rejected by the spike.", ex.Message, StringComparison.Ordinal);

        Assert.Equal(EffortLevel.Max, AnswerSettings.Create(ModelIds.ClaudeOpus5, EffortLevel.Max, excluded).Effort);
        Assert.Equal(EffortLevel.XHigh, AnswerSettings.Create(ModelIds.ClaudeSonnet5, EffortLevel.XHigh, excluded).Effort);
    }

    [Theory]
    [InlineData(EffortLevel.Low, "Low")]
    [InlineData(EffortLevel.Medium, "Medium")]
    [InlineData(EffortLevel.High, "High")]
    [InlineData(EffortLevel.XHigh, "Extra high")]
    [InlineData(EffortLevel.Max, "Max")]
    public void EachLevel_HasItsDisplayName(EffortLevel level, string name) =>
        Assert.Equal(name, level.ToDisplayName());
}
