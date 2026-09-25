using DocQaMs.Core.Conversations;
using DocQaMs.Web.Components.Conversations;

namespace DocQaMs.Web.Tests.Conversations;

public sealed class AnswerSectionsTests
{
    [Fact]
    public void AnAnswerWithBothHeadings_IsSplit_AndTheHeadingsAndTheirBlankLinesDropped()
    {
        AnswerSections sections = AnswerSections.Split(
        [
            new AnswerSegment("Summary\nA sump must hold 5 gallons.\n\nDetail\nUnder Section 175.410(b)(2), ", []),
            new AnswerSegment("the sump must hold at least 5 gallons.", [1]),
            new AnswerSegment("\n\n", []),
        ]);

        AnswerSegment summary = Assert.Single(sections.Summary);
        Assert.Equal("A sump must hold 5 gallons.", summary.Text);
        Assert.Empty(summary.SourceNumbers);
        Assert.Equal(["Under Section 175.410(b)(2), ", "the sump must hold at least 5 gallons."], sections.Detail.Select(segment => segment.Text));
        Assert.Equal([1], sections.Detail[1].SourceNumbers);
    }

    [Fact]
    public void ABlockTheDetailHeadingFallsInside_KeepsItsMarkersWithTheDetail()
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment("Summary\nYes.\nDetail\nIt must.", [2, 3])]);

        Assert.Equal("Yes.", Assert.Single(sections.Summary).Text);
        AnswerSegment detail = Assert.Single(sections.Detail);
        Assert.Equal("It must.", detail.Text);
        Assert.Equal([2, 3], detail.SourceNumbers);
    }

    [Fact]
    public void AnAnswerWithoutHeadings_IsReturnedWhole_AndUnchanged()
    {
        AnswerSegment[] segments = [new AnswerSegment("\nA sump must hold 5 gallons.\n", [1])];

        AnswerSections sections = AnswerSections.Split(segments);

        Assert.Same(segments, sections.Summary);
        Assert.Empty(sections.Detail);
    }

    [Theory]
    [InlineData("**Summary**\nYes.\n**Detail**\nIt must.")]
    [InlineData("## Summary\nYes.\n## Details\nIt must.")]
    [InlineData("SUMMARY:\r\nYes.\r\nDetail:\r\nIt must.")]
    public void HeadingsWithMarkdownOrAColon_AreStillRecognized(string text)
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment(text, [])]);

        Assert.Equal("Yes.", Assert.Single(sections.Summary).Text);
        Assert.Equal("It must.", Assert.Single(sections.Detail).Text);
    }

    [Fact]
    public void TheWordsInsideALine_AreNotHeadings()
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment("Summary\nIn Detail the rule is strict.\nThe Summary: it holds.", [])]);

        Assert.Equal("In Detail the rule is strict.\nThe Summary: it holds.", Assert.Single(sections.Summary).Text);
        Assert.Empty(sections.Detail);
    }

    [Fact]
    public void AHeadingAfterTheDetailHeading_IsOrdinaryDetailText()
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment("Summary\nYes.\nDetail\nIt must.\nSummary\nIt holds.\nDetail\nDone.", [])]);

        Assert.Equal("Yes.", Assert.Single(sections.Summary).Text);
        Assert.Equal("It must.\nSummary\nIt holds.\nDetail\nDone.", Assert.Single(sections.Detail).Text);
    }

    [Fact]
    public void AHeadingWordAtTheStartOfABlock_MidLine_IsNotAHeading()
    {
        // The second block continues the first one's line, so its "Detail" is not a line of its own.
        AnswerSections sections = AnswerSections.Split(
        [
            new AnswerSegment("Summary\nSee the ", []),
            new AnswerSegment("Detail\n", []),
            new AnswerSegment("below.", []),
        ]);

        Assert.Equal("See the Detail\nbelow.", string.Concat(sections.Summary.Select(segment => segment.Text)));
        Assert.Empty(sections.Detail);
    }

    [Fact]
    public void AStreamingAnswer_WithOnlyItsSummarySoFar_HasNoDetail()
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment("Summary\nA sump must", [])]);

        Assert.Equal("A sump must", Assert.Single(sections.Summary).Text);
        Assert.Empty(sections.Detail);
    }

    [Fact]
    public void AStreamingAnswer_WhoseLastLineIsTheDetailHeading_HasAnEmptyDetail()
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment("Summary\nYes.\n\nDetail", [])]);

        Assert.Equal("Yes.", Assert.Single(sections.Summary).Text);
        Assert.Empty(sections.Detail);
    }

    [Fact]
    public void AnAnswerWithAnEmptySummary_ShowsItsDetailAsTheAnswer()
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment("Summary\n\nDetail\nIt must.", [1])]);

        Assert.True(sections.HasSummaryText);
        AnswerSegment only = Assert.Single(sections.Summary);
        Assert.Equal("It must.", only.Text);
        Assert.Equal([1], only.SourceNumbers);
        Assert.Empty(sections.Detail);
    }

    [Fact]
    public void AnAnswerWithOnlyADetailHeading_TakesTheTextBeforeItAsTheSummary()
    {
        AnswerSections sections = AnswerSections.Split([new AnswerSegment("Yes.\nDetail\nIt must.", [])]);

        Assert.Equal("Yes.", Assert.Single(sections.Summary).Text);
        Assert.Equal("It must.", Assert.Single(sections.Detail).Text);
    }

    [Fact]
    public void NoSegments_HaveNoSummaryText() =>
        Assert.False(AnswerSections.Split([]).HasSummaryText);
}
