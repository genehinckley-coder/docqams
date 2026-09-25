using System.Diagnostics;
using System.Text;
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
/// Plan item 11's live acceptance check, and the plan's first success criterion: the prototype's sump
/// question, asked on the real corpus at the default settings, returns a cited answer naming Section
/// 175.410 with every quote matched.
/// </summary>
/// <remarks>
/// <para>
/// Billed: about $2.16 cold on Opus 5 at <c>high</c>, or about $0.23 if another run has warmed the
/// corpus entry within the hour. Skipped unless <c>DOCQA_LIVE=1</c>.
/// </para>
/// <para>
/// It goes through the production path end to end — <see cref="CorpusLoader"/> with <see cref="ClaudeTokenCounter"/>, the real
/// <see cref="CacheAffecting.SystemPrompt"/>, <see cref="AnswerService"/> over <see cref="ClaudeChatClientProvider"/>, and
/// <see cref="QuoteChecker"/> — so it writes and reads the production corpus cache entry, unlike the
/// item 1 spike, which varies its system prompt to stay cold.
/// </para>
/// <para>
/// In the non-parallel collection, because another live test sets process-wide ANTHROPIC_* variables
/// that a client built here could read.
/// </para>
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SumpAnswerLiveTests
{
    /// <summary>The prototype's question, verbatim from its <c>SUMP_QUESTION</c>.</summary>
    private const string SumpQuestion = "what are the sump requirements for a UST in Illinois";

    /// <summary>The section the prototype's answer cited, and the plan's stated acceptance criterion.</summary>
    private const string ExpectedSection = "175.410";

    [Fact]
    public async Task TheSumpQuestion_CitesSection175410_WithEveryQuoteMatched()
    {
        LiveApi.RequireEnabled();
        CancellationToken ct = TestContext.Current.CancellationToken;

        var capture = new CaptureHandler();
        using ClaudeChatClientProvider provider = LiveApi.CreateProvider(capture);

        // Free: count_tokens is not billed. The size guard runs here as it does in production.
        var loader = new CorpusLoader(new ClaudeTokenCounter(() => provider), NullLogger<CorpusLoader>.Instance);
        CorpusSnapshot corpus = await loader.AssembleAsync(
            CorpusExtractor.ExtractCorpus(RepoPaths.Combine("admincode")), LiveApi.Options.TokenCeiling, ct);

        IOptions<DocQaOptions> options = Options.Create(LiveApi.Options);
        AnswerSettings settings = AnswerSettings.Default;

        // The run's budget is checked against what this question is expected to cost, not against zero.
        CostEstimate estimate = new CostEstimator(options).Estimate(settings, corpus);
        LiveApi.Log($"item 11 sump: expected ${estimate.ColdQuestion.Lowest:F2}-${estimate.ColdQuestion.Highest:F2} cold, " +
            $"${estimate.WarmInput:F2} input alone if the entry is still warm");
        LiveApi.RequireBudget(estimate.ColdQuestion.Highest);

        var answers = new AnswerService(
            () => provider, options, TimeProvider.System, NullLogger<AnswerService>.Instance);
        List<ChatMessage> messages = [MessageRequestBuilder.FirstTurn(corpus.RequestDocuments, SumpQuestion)];

        // The level's own timeout is 20 minutes. At the measured ~95 output tokens a second, the spike's
        // 3-minute cap still leaves about three times the measured 55-67 seconds.
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(LiveApi.RequestCap);

        var text = new StringBuilder();
        AnswerResult? result = null;

        // What the request is known to have been billed so far, so that a request which never completes
        // still counts against the run's budget. Output before the end is never reported.
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
                    case AnswerEvent.TextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case AnswerEvent.Completed completed:
                        result = completed.Result;
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception exception) when (result is null)
        {
            LiveApi.RecordCost("item 11 sump", billed);
            if (exception is OperationCanceledException && cap.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The sump answer ran past the {LiveApi.RequestCap.TotalMinutes:F0}-minute cap and was stopped.", exception);
            }

            throw;
        }

        Assert.NotNull(result);
        LiveApi.Record("item 11 sump", settings.ModelId, settings.Effort, result.Response, Stopwatch.GetElapsedTime(started));
        LiveApi.LogTotal();
        LiveApi.Log(text.ToString());

        // The streamed text and the aggregated message agree, so either may be read from here on.
        string answer = string.Concat(result.Reply.Contents.OfType<TextContent>().Select(textContent => textContent.Text));
        Assert.Equal(answer, text.ToString());

        Assert.Null(result.StopWarning);
        Assert.Contains(ExpectedSection, answer, StringComparison.Ordinal);

        // Cold or warm, the corpus prefix must have been cached: a full-price answer is a shape failure.
        Assert.True(result.Usage.HasCacheActivity, AnswerResult.NotCachedWarning);

        var checker = new QuoteChecker(options);
        QuoteReport report = checker.BuildReport(result, corpus);

        LiveApi.Log($"item 11 sump: {report.MatchedCount}/{report.Checks.Count} distinct spans matched, " +
            $"{report.CitationCount} citations, {report.UncitedFraction:P0} of the answer uncited");

        Assert.NotEmpty(report.Checks);

        // Every distinct span, including any citation that could not be read at all. Assert.True carries the
        // failing quotes into the message; Assert.Empty would report only that the list was not empty.
        string failures = string.Join(
            Environment.NewLine,
            report.Failures.Select(failure =>
                $"[{failure.SourceNumber}] {failure.Check.Status}: {failure.Check.Detail} (quote: {failure.Check.CitedText})"));
        Assert.True(
            report.Failures.Count == 0,
            $"{report.Failures.Count} of {report.Checks.Count} distinct spans did not match:{Environment.NewLine}{failures}");
    }
}
