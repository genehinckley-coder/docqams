using Anthropic.Models.Messages;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace DocQaMs.Claude.Tests.Live;

/// <summary>
/// Support for billed tests against the real Messages API, through the production Claude pipeline. They
/// are skipped unless <c>DOCQA_LIVE=1</c>, and they run with the production options from the web app's
/// <c>appsettings.json</c>: the real key file, and each level's <c>max_tokens</c> and timeout.
/// </summary>
internal static class LiveApi
{
    public const string Variable = "DOCQA_LIVE";

    // Below its model's minimum a cache breakpoint is silently ignored, and every caching check would
    // fail for a fixture reason.
    private static readonly Dictionary<string, long> MinimumCacheablePrefix = new(StringComparer.Ordinal)
    {
        [ModelIds.ClaudeOpus5] = 512,

        // Not yet published; assumed equal to Opus 5, whose feature set it shares.
        [ModelIds.ClaudeOpus55] = 512,
        [ModelIds.ClaudeSonnet5] = 1_024,
    };

    private static readonly Lock LogLock = new();
    private static decimal _totalCost;

    public static DocQaOptions Options { get; } = TestSettings.Bind(new ConfigurationBuilder()
        .AddJsonFile(RepoPaths.Combine("src", "DocQaMs.Web", "appsettings.json"), optional: false, reloadOnChange: false)
        .Build());

    private static string LogPath { get; } = RepoPaths.Combine("TestResults", "live.log");

    public static void RequireEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Variable) == "1",
            $"Billed live API test; set {Variable}=1 to run it.");

    public static ClaudeChatClientProvider CreateProvider(CaptureHandler capture) =>
        ClaudeChatClientProvider.Create(ApiKeyLoader.Load(Options.ApiKeyPath), Options.RequestTimeouts, [capture]);

    /// <summary>
    /// The production system prompt plus a per-run id. The id makes each run start cold, so a rerun
    /// within the hour cannot turn an expected cache write into a read. It is exactly the kind of
    /// variation the production prompt must never contain.
    /// </summary>
    public static string NewSystemPrompt() => $"{CacheAffecting.SystemPrompt}\n\nSpike run id: {Guid.NewGuid():N}";

    public static ChatRequest Request(
        string modelId, EffortLevel level, string systemPrompt, IReadOnlyList<ChatMessage> messages) =>
        MessageRequestBuilder.Create(modelId, level, Options.MaxTokens[level], systemPrompt, messages);

    /// <summary>
    /// Two free <c>count_tokens</c> calls, before any billed one. <see cref="ClaudeTokenCounter"/> always
    /// counts the production system prompt, so the per-run prompt is counted through the SDK directly, with
    /// the first question rendered by the same mapper.
    /// </summary>
    public static async Task AssertFixturesCacheableAsync(ClaudeChatClientProvider provider, string systemPrompt, CancellationToken ct)
    {
        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            MessageTokensCount count = await provider.AnthropicClient.Messages.CountTokens(
                new MessageCountTokensParams
                {
                    Model = model.Id,
                    System = systemPrompt,
                    Messages = [ClaudeMessageMapper.ToMessageParam(MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "?"))],
                },
                ct);

            Log($"fixture prefix on {model.Id}: {count.InputTokens:N0} tokens (cacheable minimum {MinimumCacheablePrefix[model.Id]:N0})");
            Assert.True(
                count.InputTokens > MinimumCacheablePrefix[model.Id],
                $"The fixtures count {count.InputTokens} tokens on {model.Id}, not above its cacheable minimum. Enlarge them.");
        }
    }

    /// <summary>
    /// The most one live test run may spend; after it, further billed calls are refused. It covers the
    /// item 1 spike (about $1) and item 11's sump acceptance answer (about $2.16), with headroom for the
    /// cold corpus write being larger than measured.
    /// </summary>
    public const decimal RunBudget = 4.00m;

    /// <summary>
    /// Refuses a billed call that would take the run past <see cref="RunBudget"/>. Called by
    /// <see cref="StreamAsync"/>, and directly by tests that send through the production answer service.
    /// </summary>
    /// <param name="expected">
    /// What this call is expected to cost, when that is known before sending. The budget is a cap on what
    /// the run may start, not on what it may be billed: a call that costs more than expected is discovered
    /// only after it has been paid for.
    /// </param>
    /// <exception cref="InvalidOperationException">The call would take the run past its budget.</exception>
    public static void RequireBudget(decimal expected = 0m)
    {
        lock (LogLock)
        {
            if (_totalCost + expected >= RunBudget)
            {
                throw new InvalidOperationException(
                    $"This live run has spent ${_totalCost:F2}, and a call expected to cost ${expected:F2} would " +
                    $"reach its ${RunBudget:F2} budget; it is refused.");
            }
        }
    }

    /// <summary>
    /// Adds a cost to the run's total without a completed response: what a stopped, timed-out or failed
    /// request is known to have been billed, from the usage that arrived before it ended.
    /// </summary>
    /// <param name="label">What was being asked.</param>
    /// <param name="cost">The cost to count.</param>
    public static void RecordCost(string label, decimal cost)
    {
        lock (LogLock)
        {
            _totalCost += cost;
        }

        Log($"{label}: did not complete; ${cost:F4} of input-side usage had already been reported");
    }

    /// <summary>
    /// The longest one live request may run. At the measured ~95 output tokens a second, it bounds a
    /// runaway answer at about 17,000 tokens (about $0.45 on Opus 5). <c>max_tokens</c> keeps each level's
    /// production value, because check 7 must send 64,000 and 128,000.
    /// </summary>
    public static readonly TimeSpan RequestCap = TimeSpan.FromMinutes(3);

    /// <summary>
    /// One pass over the chat client's stream, handing each update to <paramref name="onUpdate"/> and
    /// aggregating the response per block as the answer service does; refused once the run has spent
    /// <see cref="RunBudget"/>, and stopped if it runs past <see cref="RequestCap"/>. Cancelling
    /// <paramref name="ct"/> still surfaces as an <see cref="OperationCanceledException"/>, so the Stop test
    /// is unaffected.
    /// </summary>
    /// <returns>The response, and the raw event names in the order they arrived.</returns>
    public static async Task<(ChatResponse Response, List<string> EventNames)> StreamAsync(
        IChatClient client,
        ChatRequest request,
        string modelId,
        CancellationToken ct,
        Action<ChatResponseUpdate>? onUpdate = null)
    {
        RequireBudget();

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(RequestCap);
        var reader = new AnswerStreamReader(Options.Prices[modelId]);
        List<string> names = [];
        try
        {
            await ClaudeTestClient.StreamAsync(client, request, cap.Token, update =>
            {
                names.Add(ClaudeTestClient.Name(update));
                reader.Read(update);
                onUpdate?.Invoke(update);
            });
            reader.Finish();
            return (reader.Response(), names);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log($"a request ran past the {RequestCap.TotalMinutes:F0}-minute cap and was stopped; its cost is not counted");
            throw new TimeoutException($"A live request ran past the spike's {RequestCap.TotalMinutes:F0}-minute cap and was stopped.");
        }
    }

    /// <summary>A response's usage in billing buckets.</summary>
    public static TokenUsage Usage(ChatResponse response) => TokenUsage.From(response.Usage ?? new UsageDetails());

    /// <summary>The input side of the usage, from the first <see cref="UsageContent"/> of a stream.</summary>
    public static TokenUsage InputUsage(UsageContent usage) => TokenUsage.InputSide(usage.Details);

    /// <summary>The provider's raw stop reason, such as <c>end_turn</c>.</summary>
    public static string? StopReason(ChatResponse response) => StopWarning.StopReasonOf(response);

    public static decimal Cost(string modelId, ChatResponse response) =>
        TurnCost.For(Usage(response), Options.Prices[modelId]).Total;

    /// <summary>Logs one response's usage and cost, and adds the cost to the run's total.</summary>
    public static void Record(string label, string modelId, EffortLevel level, ChatResponse response, TimeSpan elapsed)
    {
        TokenUsage usage = Usage(response);
        decimal cost = Cost(modelId, response);
        lock (LogLock)
        {
            _totalCost += cost;
        }

        int thinkingBlocks = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<TextReasoningContent>()
            .Count(reasoning => !ReplayConverter.IsRedacted(reasoning));
        Log(
            $"{label}: {modelId} {level.ToWireValue()} {elapsed.TotalSeconds:F0}s stop={StopReason(response)} " +
            $"in={usage.Input:N0} write1h={usage.CacheWrite1h:N0} write5m={usage.CacheWrite5m:N0} " +
            $"read={usage.CacheRead:N0} out={usage.Output:N0} thinking-blocks={thinkingBlocks} ${cost:F4}");
    }

    public static void LogTotal()
    {
        lock (LogLock)
        {
            Log($"running total this test run: ${_totalCost:F4}");
        }
    }

    /// <summary>Writes to the test output and to <c>TestResults/live.log</c> (gitignored).</summary>
    public static void Log(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        lock (LogLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
    }
}
