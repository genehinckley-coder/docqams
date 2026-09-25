using System.Diagnostics;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Quotes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CorpusSnapshot = DocQaMs.Core.Corpus.Corpus;

namespace DocQaMs.Claude.Tests.Live;

/// <summary>
/// Measures Claude Opus 5.5 the way Opus 5 at <c>high</c> was measured: the sump question, then one follow-up
/// while the corpus entry is warm. The figures go to <c>TestResults/live.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// The level is <c>DOCQA_LIVE_OPUS55_LEVEL</c> (<c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>;
/// <c>high</c> when unset). Each level has its own corpus entry: the first question writes it (about $1.60), and a
/// rerun within the hour reads it instead. Either way the output tokens, which are what this measures, are the same.
/// </para>
/// <para>
/// Billed: about $1.65–$1.80 for a cold question and $0.10–$0.25 for the follow-up at <c>high</c>. Skipped unless
/// both <c>DOCQA_LIVE=1</c> and <c>DOCQA_LIVE_OPUS55=1</c> are set, so an ordinary live run does not also pay for a
/// cold Opus 5.5 corpus write.
/// </para>
/// <para>
/// Costs are the measured token counts priced at <c>appsettings.json</c>'s rates; the API does not return
/// prices.
/// </para>
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class Opus55MeasurementLiveTests
{
    private const string Variable = "DOCQA_LIVE_OPUS55";

    private const string LevelVariable = "DOCQA_LIVE_OPUS55_LEVEL";

    private const string SumpQuestion = "what are the sump requirements for a UST in Illinois";

    private const string FollowUpQuestion = "What testing do those sumps need, and how often?";

    [Fact]
    public async Task TheSumpQuestion_ThenAFollowUp_OnOpus55()
    {
        LiveApi.RequireEnabled();
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Variable) == "1",
            $"Billed Opus 5.5 measurement; set {Variable}=1 as well to run it.");
        CancellationToken ct = TestContext.Current.CancellationToken;

        string levelName = Environment.GetEnvironmentVariable(LevelVariable) is { Length: > 0 } set ? set : "high";
        EffortLevel level = CacheAffecting.EffortLevels.SingleOrDefault(candidate => candidate.ToWireValue() == levelName);
        Assert.True(
            level.ToWireValue() == levelName,
            $"{LevelVariable} is '{levelName}', not one of {string.Join(", ", CacheAffecting.EffortLevels.Select(candidate => candidate.ToWireValue()))}.");

        var capture = new CaptureHandler();
        using ClaudeChatClientProvider provider = LiveApi.CreateProvider(capture);

        var loader = new CorpusLoader(new ClaudeTokenCounter(() => provider), NullLogger<CorpusLoader>.Instance);
        CorpusSnapshot corpus = await loader.AssembleAsync(
            CorpusExtractor.ExtractCorpus(RepoPaths.Combine("admincode")), LiveApi.Options.TokenCeiling, ct);
        LiveApi.Log($"opus 5.5: corpus is {corpus.TokenCounts[ModelIds.ClaudeOpus55]:N0} tokens " +
            $"(Opus 5: {corpus.TokenCounts[ModelIds.ClaudeOpus5]:N0})");

        IOptions<DocQaOptions> options = Options.Create(LiveApi.Options);
        AnswerSettings settings = AnswerSettings.Create(ModelIds.ClaudeOpus55, level);
        var answers = new AnswerService(() => provider, options, TimeProvider.System, NullLogger<AnswerService>.Instance);
        string label = $"opus 5.5 {levelName}";

        CostEstimate estimate = new CostEstimator(options).Estimate(settings, corpus);
        LiveApi.Log($"{label} first: expected ${estimate.ColdQuestion.Lowest:F2}-${estimate.ColdQuestion.Highest:F2} if cold");
        LiveApi.RequireBudget(estimate.ColdQuestion.Highest);

        List<ChatMessage> messages = [MessageRequestBuilder.FirstTurn(corpus.RequestDocuments, SumpQuestion)];
        AnswerResult first = await AskAsync(answers, settings, messages, $"{label} first", ct);

        // Within the hour after an earlier run at this level, the first question reads the corpus instead.
        long corpusTouched = first.Usage.CacheWrite1h + first.Usage.CacheRead;
        LiveApi.Log($"{label} first: corpus {(first.Usage.CacheWrite1h > 0 ? "written (cold)" : "read (warm)")}");
        Assert.Null(first.StopWarning);
        Assert.True(corpusTouched >= corpus.TokenCounts[ModelIds.ClaudeOpus55] / 2, "The first question neither wrote nor read the corpus entry.");
        Assert.Contains("175.410", AnswerText(first), StringComparison.Ordinal);
        LogQuotes($"{label} first", new QuoteChecker(options).BuildReport(first, corpus));

        messages.Add(MessageRequestBuilder.AssistantTurn(ReplayConverter.ToReplay(first.Reply)));
        messages.Add(MessageRequestBuilder.FollowUpTurn(FollowUpQuestion));
        LiveApi.RequireBudget(0.50m);
        AnswerResult followUp = await AskAsync(answers, settings, messages, $"{label} follow-up", ct);

        Assert.Null(followUp.StopWarning);
        Assert.True(
            followUp.Usage.CacheRead >= corpus.TokenCounts[ModelIds.ClaudeOpus55] / 2,
            $"The follow-up read only {followUp.Usage.CacheRead:N0} cached tokens; the corpus entry was not reused.");
        LogQuotes($"{label} follow-up", new QuoteChecker(options).BuildReport(followUp, corpus));

        LiveApi.LogTotal();
    }

    private static async Task<AnswerResult> AskAsync(
        AnswerService answers, AnswerSettings settings, List<ChatMessage> messages, string label, CancellationToken ct)
    {
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(LiveApi.RequestCap);

        AnswerResult? result = null;
        decimal billed = 0m;
        long started = Stopwatch.GetTimestamp();
        try
        {
            await foreach (AnswerEvent answerEvent in answers.StreamAsync(settings, messages, cap.Token))
            {
                switch (answerEvent)
                {
                    case AnswerEvent.UsageStarted usage:
                        billed = usage.Cost.Total;
                        break;
                    case AnswerEvent.Completed completed:
                        result = completed.Result;
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception) when (result is null)
        {
            LiveApi.RecordCost(label, billed);
            throw;
        }

        Assert.NotNull(result);
        LiveApi.Record(label, settings.ModelId, settings.Effort, result.Response, Stopwatch.GetElapsedTime(started));
        LiveApi.Log($"{label}: {result.Usage.Output:N0} output tokens, ${result.Cost.Total:F4} at the configured prices");
        LiveApi.Log(AnswerText(result));
        return result;
    }

    private static string AnswerText(AnswerResult result) =>
        string.Concat(result.Reply.Contents.OfType<TextContent>().Select(text => text.Text));

    private static void LogQuotes(string label, QuoteReport report) =>
        LiveApi.Log($"{label}: {report.MatchedCount}/{report.Checks.Count} distinct spans matched, " +
            $"{report.CitationCount} citations, {report.UncitedFraction:P0} of the answer uncited");
}
