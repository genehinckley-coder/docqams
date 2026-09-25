using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Every stop reason is checked, through the answer service, with the real per-level output limits:
/// 32,000 for Low to High, 64,000 at Extra high, and 128,000 at Max.
/// </summary>
public sealed class StopWarningTests
{
    [Fact]
    public async Task EndTurn_CarriesNoWarning()
    {
        AnswerResult result = await Finish(EffortLevel.High, "end_turn", Answered);

        Assert.Null(result.StopWarning);
    }

    [Fact]
    public async Task MaxTokens_AfterTheAnswerStarted_NamesTheLimit_AndSuggestsAHigherLimitOnly()
    {
        StopWarning warning = Assert.IsType<StopWarning>((await Finish(EffortLevel.High, "max_tokens", Answered)).StopWarning);

        Assert.Equal(StopWarningKind.Truncated, warning.Kind);
        Assert.StartsWith("TRUNCATED", warning.Message, StringComparison.Ordinal);
        Assert.Contains("32,000-token output limit for High effort", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Its citations point at real text", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Ask a narrower question", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Extra high effort, whose limit is 64,000 tokens", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("lower level", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxTokens_InsideThinking_AlsoSuggestsALowerLevel()
    {
        StopWarning warning = Assert.IsType<StopWarning>((await Finish(EffortLevel.High, "max_tokens", StillThinking)).StopWarning);

        Assert.Contains("still thinking", warning.Message, StringComparison.Ordinal);
        Assert.Contains("lower level", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Extra high effort, whose limit is 64,000 tokens", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxTokens_AtMax_SuggestsNoHigherLimit_ForThereIsNone()
    {
        StopWarning answered = Assert.IsType<StopWarning>((await Finish(EffortLevel.Max, "max_tokens", Answered)).StopWarning);
        StopWarning thinking = Assert.IsType<StopWarning>((await Finish(EffortLevel.Max, "max_tokens", StillThinking)).StopWarning);

        Assert.Contains("128,000-token output limit for Max effort", answered.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new conversation", answered.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("whose limit is", thinking.Message, StringComparison.Ordinal);
        Assert.Contains("lower level", thinking.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxTokens_AtLow_SuggestsNoLowerLevel_AndSkipsLevelsWithTheSameLimit()
    {
        StopWarning warning = Assert.IsType<StopWarning>((await Finish(EffortLevel.Low, "max_tokens", StillThinking)).StopWarning);

        Assert.DoesNotContain("lower level", warning.Message, StringComparison.Ordinal);

        // Medium and High share Low's 32,000 limit, so they would not help.
        Assert.Contains("Extra high effort, whose limit is 64,000 tokens", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_IsReportedWithItsCategory()
    {
        StopWarning warning = Assert.IsType<StopWarning>(
            (await Finish(EffortLevel.High, "refusal", Answered, "cyber", "Declined on policy grounds.")).StopWarning);

        Assert.Equal(StopWarningKind.Refused, warning.Kind);
        Assert.StartsWith("REFUSED", warning.Message, StringComparison.Ordinal);
        Assert.Contains("category 'cyber'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Declined on policy grounds.", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not an answer", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_WithoutDetails_IsStillReported()
    {
        StopWarning warning = Assert.IsType<StopWarning>((await Finish(EffortLevel.High, "refusal", Answered)).StopWarning);

        Assert.Equal(StopWarningKind.Refused, warning.Kind);
        Assert.DoesNotContain("category", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop_sequence")]
    [InlineData("pause_turn")]
    [InlineData("model_context_window_exceeded")]
    [InlineData("a_reason_added_later")]
    public async Task AnyOtherStopReason_IsReportedAsIncomplete(string stopReason)
    {
        StopWarning warning = Assert.IsType<StopWarning>((await Finish(EffortLevel.High, stopReason, Answered)).StopWarning);

        Assert.Equal(StopWarningKind.Incomplete, warning.Kind);
        Assert.StartsWith("INCOMPLETE", warning.Message, StringComparison.Ordinal);
        Assert.Contains($"'{stopReason}'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStopReason_IsReportedAsIncomplete()
    {
        StopWarning warning = Assert.IsType<StopWarning>((await Finish(EffortLevel.High, null, Answered)).StopWarning);

        Assert.Equal(StopWarningKind.Incomplete, warning.Kind);
        Assert.Contains("no stop_reason", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("end_turn", null)]
    [InlineData("max_tokens", StopWarningKind.Truncated)]
    [InlineData("refusal", StopWarningKind.Refused)]
    [InlineData("stop_sequence", StopWarningKind.Incomplete)]
    public void TheRawStopReason_IsAuthoritative_OverTheFinishReason(string stopReason, StopWarningKind? kind)
    {
        // Stop summarizes both end_turn and stop_sequence, so only the raw reason tells them apart.
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "An answer."))
        {
            FinishReason = ChatFinishReason.Stop,
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.StopReason] = stopReason },
        };

        Assert.Equal(stopReason, StopWarning.StopReasonOf(response));
        Assert.Equal(kind, StopWarning.For(response, EffortLevel.High, TestSettings.ValidOptions().MaxTokens)?.Kind);
    }

    public static TheoryData<string, string, StopWarningKind?> FinishReasons => new()
    {
        { "stop", "end_turn", null },
        { "length", "max_tokens", StopWarningKind.Truncated },
        { "content_filter", "refusal", StopWarningKind.Refused },
        { "tool_calls", "tool_calls", StopWarningKind.Incomplete },
    };

    [Theory]
    [MemberData(nameof(FinishReasons))]
    public void WithoutARawStopReason_TheFinishReasonStandsForIt(string finishReason, string stopReason, StopWarningKind? kind)
    {
        // A provider that reports no raw stop reason is still checked.
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "An answer."))
        {
            FinishReason = new ChatFinishReason(finishReason),
        };

        Assert.Equal(stopReason, StopWarning.StopReasonOf(response));
        Assert.Equal(kind, StopWarning.For(response, EffortLevel.High, TestSettings.ValidOptions().MaxTokens)?.Kind);
    }

    [Fact]
    public void NeitherAStopReasonNorAFinishReason_IsIncomplete()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "An answer."));

        Assert.Null(StopWarning.StopReasonOf(response));
        Assert.Equal(
            "INCOMPLETE: the response ended with no stop_reason, so the answer may stop partway.",
            StopWarning.For(response, EffortLevel.High, TestSettings.ValidOptions().MaxTokens)?.Message);
    }

    private static IEnumerable<ChatResponseUpdate> Answered =>
    [
        .. NeutralUpdates.Thinking(0, "Checking Part A."),
        .. NeutralUpdates.Text(1, "Sumps must be ti", NeutralUpdates.Citation(0, 10)),
    ];

    private static IEnumerable<ChatResponseUpdate> StillThinking =>
    [
        .. NeutralUpdates.Text(0, ""),
        .. NeutralUpdates.Thinking(1, "Checking Part A, then Part B, then"),
    ];

    private static async Task<AnswerResult> Finish(
        EffortLevel effort,
        string? stopReason,
        IEnumerable<ChatResponseUpdate> blocks,
        string? refusalCategory = null,
        string? refusalExplanation = null)
    {
        List<ChatResponseUpdate> updates =
        [
            NeutralUpdates.Start(),
            .. blocks,
            .. NeutralUpdates.End(stopReason, refusalCategory: refusalCategory, refusalExplanation: refusalExplanation),
        ];
        using var harness = new AnswerHarness(updates);
        return await harness.CompleteAsync(
            AnswerSettings.Create(ModelIds.ClaudeOpus5, effort), cancellationToken: TestContext.Current.CancellationToken);
    }
}
