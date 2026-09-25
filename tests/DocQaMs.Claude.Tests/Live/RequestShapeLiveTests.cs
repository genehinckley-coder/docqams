using System.Diagnostics;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Text;
using Microsoft.Extensions.AI;

namespace DocQaMs.Claude.Tests.Live;

/// <summary>
/// Plan item 1, the request-shape spike, against the real API through the production Claude pipeline.
/// Billed (about $1 in all) and skipped unless <c>DOCQA_LIVE=1</c>. Each response's usage and cost is
/// logged to <c>TestResults/live.log</c>.
/// </summary>
/// <remarks>
/// In the non-parallel collection: one test sets process-wide ANTHROPIC_* variables, and the others
/// build clients that could read them. Each test uses its own system-prompt run id, so no test depends
/// on another's cache entries.
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RequestShapeLiveTests
{
    // Every ANTHROPIC_* variable named in the SDK 12.47.0 assembly.
    private static readonly string[] AnthropicVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_PROFILE",
        "ANTHROPIC_WORKSPACE_ID", "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_IDENTITY_TOKEN",
        "ANTHROPIC_IDENTITY_TOKEN_FILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_SERVICE_ACCOUNT_ID",
        "ANTHROPIC_WEBHOOK_SIGNING_KEY",
    ];

    // Each asks something different, so a cache read can only come from the shared document prefix.
    private const string SumpsAndPermits =
        "What do these documents require for tank containment sumps, and when does work affecting a sump require a permit? Be concise.";

    private const string SumpTesting =
        "How often must a tank containment sump be tested, how is the test performed, and what happens if a sump fails? Be concise.";

    private const string Exemptions = "Which activities are exempt from the permit requirement? Answer in two or three sentences.";

    private const string RecordsAndPermits = "What records must be kept, and how long is a permit valid? Be concise.";

    private const string Capacity = "In one sentence: what capacity must a tank containment sump have?";

    private const string PermitDuration = "In one sentence: how long is a permit valid?";

    private const string TestFrequency = "In one sentence: how often must a tank containment sump be tested?";

    private const string SensorPosition = "In one sentence: where must a sump's liquid sensor be positioned?";

    private const string Everything = "Explain every requirement in both documents in detail, section by section.";

    private const string Opus = ModelIds.ClaudeOpus5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Check 1, live and free: ambient variables change neither the host nor the credentials.</summary>
    [Fact]
    public async Task AmbientAnthropicVariables_ChangeNothing_AgainstTheRealApi()
    {
        LiveApi.RequireEnabled();
        string key = ApiKeyLoader.Load(LiveApi.Options.ApiKeyPath);
        Dictionary<string, string?> saved = AnthropicVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string name in AnthropicVariables)
            {
                Environment.SetEnvironmentVariable(name, name == "ANTHROPIC_BASE_URL" ? "https://junk.invalid" : "junk-value");
            }

            var capture = new CaptureHandler();
            using ClaudeChatClientProvider provider = LiveApi.CreateProvider(capture);

            long count = await new ClaudeTokenCounter(() => provider).CountAsync(Opus, SpikeFixtures.Documents, Ct);

            // Success itself shows the file's key was used: a junk key or bearer token would be a 401.
            Assert.True(count > 0);
            CapturedRequest request = Assert.Single(capture.Requests);
            Assert.Equal("https", request.Uri?.Scheme);
            Assert.Equal("api.anthropic.com", request.Uri?.Host);
            Assert.True(string.Equals(key, request.ApiKey, StringComparison.Ordinal), "The key sent is not the key file's key.");
            Assert.False(request.HasAuthorization);
            Assert.DoesNotContain(request.Headers, header => header.Value.Contains("junk", StringComparison.Ordinal));
            Assert.DoesNotContain(request.Headers, header =>
                header.Name.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                || header.Name.Contains("organization", StringComparison.OrdinalIgnoreCase));
            LiveApi.Log($"credentials: count_tokens succeeded with every ANTHROPIC_* variable set to junk ({count:N0} tokens)");
        }
        finally
        {
            foreach ((string name, string? value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>
    /// Checks 2 to 6 on the prototype's own combination: one pass over the stream, offsets, the replayed
    /// turn 2, the prefix cache, and usage read at the start of the response.
    /// </summary>
    [Fact]
    public async Task Opus5AtHigh_StreamsInOnePass_CachesThePrefix_AndAcceptsTheReplayedTurn()
    {
        LiveApi.RequireEnabled();
        var capture = new CaptureHandler();
        using ClaudeChatClientProvider provider = LiveApi.CreateProvider(capture);
        string system = LiveApi.NewSystemPrompt();
        await LiveApi.AssertFixturesCacheableAsync(provider, system, Ct);
        IChatClient client = provider.ForEffort(EffortLevel.High);

        // Turn 1: one HTTP request, every event forwarded, and the final response complete.
        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, SumpsAndPermits);
        TokenUsage? atStart = null;
        int requestsBefore = capture.Requests.Count;
        var watch = Stopwatch.StartNew();
        (ChatResponse turn1, List<string> names) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(Opus, EffortLevel.High, system, [first]),
            Opus,
            Ct,
            update =>
            {
                if (atStart is null && update.Contents.OfType<UsageContent>().FirstOrDefault() is { } usage)
                {
                    atStart = LiveApi.InputUsage(usage);
                }
            });
        LiveApi.Record("round trip, turn 1", Opus, EffortLevel.High, turn1, watch.Elapsed);

        Assert.Equal(requestsBefore + 1, capture.Requests.Count);
        Assert.Equal("message_start", names[0]);
        Assert.Contains("message_stop", names);
        Assert.Contains("content_block_delta:citations_delta", names);
        Assert.Contains("content_block_delta:signature_delta", names);

        Assert.Equal("end_turn", LiveApi.StopReason(turn1));
        Assert.Null(turn1.AdditionalProperties?.GetValueOrDefault(ChatKeys.RefusalCategory));

        TokenUsage usage1 = LiveApi.Usage(turn1);
        Assert.True(usage1.CacheWrite1h > 0, "Turn 1 wrote no 1-hour cache entry.");
        Assert.Equal(usage1.CacheWrite1h, atStart?.CacheWrite1h);
        Assert.Equal(0, usage1.CacheRead);

        List<TextReasoningContent> thinking = [.. Contents(turn1).OfType<TextReasoningContent>().Where(reasoning => !ReplayConverter.IsRedacted(reasoning))];
        Assert.All(thinking, block => Assert.False(string.IsNullOrEmpty(block.ProtectedData)));

        // Check 3, live: every span, converted from code points, slices exactly its cited text.
        List<AnswerCitation> citations = [.. CharLocations(turn1)];
        Assert.NotEmpty(citations);
        string[] texts = [.. SpikeFixtures.Documents.Select(document => document.Text)];
        CodePointIndex[] indexes = [.. texts.Select(CodePointIndex.Build)];
        int afterNonBmp = 0;
        foreach (AnswerCitation citation in citations)
        {
            Assert.InRange(citation.DocumentIndex, 0, texts.Length - 1);
            CodePointIndex index = indexes[citation.DocumentIndex];
            Assert.InRange(citation.StartCharIndex, 0, citation.EndCharIndex - 1);
            Assert.InRange(citation.EndCharIndex, 1, index.Length);

            string text = texts[citation.DocumentIndex];
            int from = index.ToUtf16((int)citation.StartCharIndex);
            Assert.Equal(citation.CitedText, text[from..index.ToUtf16((int)citation.EndCharIndex)]);
            if (text.IndexOf(SpikeFixtures.NonBmp, StringComparison.Ordinal) is >= 0 and int nonBmp && nonBmp < from)
            {
                afterNonBmp++;
            }
        }

        LiveApi.Log($"round trip: {citations.Count} citations sliced exactly as code points; {afterNonBmp} start after the non-BMP character; {thinking.Count} thinking block(s), all signed");

        // Check 5: a different first question, in a new conversation, reads the whole prefix.
        watch.Restart();
        (ChatResponse other, _) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(Opus, EffortLevel.High, system, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, SumpTesting)]),
            Opus,
            Ct);
        LiveApi.Record("round trip, new conversation", Opus, EffortLevel.High, other, watch.Elapsed);
        Assert.Equal(usage1.CacheWrite1h, LiveApi.Usage(other).CacheRead);
        Assert.Equal(0, LiveApi.Usage(other).CacheWrite1h);

        // Check 4: turn 1's reply, converted, is accepted as history. Turn 2 reads the corpus plus turn 1's
        // tail, through the breakpoint on the previous question: a cited reply spans more than the 20
        // positions an automatic breakpoint looks back.
        List<AIContent> replay1 = ReplayConverter.ToReplay(Reply(turn1));
        ChatMessage second = MessageRequestBuilder.FollowUpTurn(Exemptions);
        LiveApi.Log($"round trip: turn 1's reply has {Contents(turn1).Count} content blocks, {replay1.Count} of them replayable");
        watch.Restart();
        (ChatResponse turn2, _) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(Opus, EffortLevel.High, system, [first, MessageRequestBuilder.AssistantTurn(replay1), second]),
            Opus,
            Ct);
        LiveApi.Record("round trip, turn 2", Opus, EffortLevel.High, turn2, watch.Elapsed);
        Assert.Equal("end_turn", LiveApi.StopReason(turn2));
        Assert.Equal(usage1.CacheWrite1h + usage1.CacheWrite5m, LiveApi.Usage(turn2).CacheRead);

        // Turn 3: the marker moves on to the second question, so turn 3 reads everything turn 2 wrote.
        List<AIContent> replay2 = ReplayConverter.ToReplay(Reply(turn2));
        watch.Restart();
        (ChatResponse turn3, _) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(
                Opus,
                EffortLevel.High,
                system,
                [
                    first,
                    MessageRequestBuilder.AssistantTurn(replay1),
                    second,
                    MessageRequestBuilder.AssistantTurn(replay2),
                    MessageRequestBuilder.FollowUpTurn(RecordsAndPermits),
                ]),
            Opus,
            Ct);
        LiveApi.Record("round trip, turn 3", Opus, EffortLevel.High, turn3, watch.Elapsed);
        Assert.Equal("end_turn", LiveApi.StopReason(turn3));
        TokenUsage usage2 = LiveApi.Usage(turn2);
        Assert.Equal(usage2.CacheRead + usage2.CacheWrite5m, LiveApi.Usage(turn3).CacheRead);
        LiveApi.LogTotal();
    }

    /// <summary>Check 6, live: cancelling the enumeration, as Stop will, ends the answer promptly.</summary>
    [Fact]
    public async Task CancellingMidAnswer_EndsTheStreamPromptly()
    {
        LiveApi.RequireEnabled();
        var capture = new CaptureHandler();
        using ClaudeChatClientProvider provider = LiveApi.CreateProvider(capture);
        string system = LiveApi.NewSystemPrompt();
        await LiveApi.AssertFixturesCacheableAsync(provider, system, Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int requestsBefore = capture.Requests.Count;
        long? write1hAtStart = null;
        var sinceStop = new Stopwatch();

        Exception? ex = await Record.ExceptionAsync(() => LiveApi.StreamAsync(
            provider.ForEffort(EffortLevel.High),
            LiveApi.Request(Opus, EffortLevel.High, system, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, Everything)]),
            Opus,
            stop.Token,
            update =>
            {
                if (write1hAtStart is null && update.Contents.OfType<UsageContent>().FirstOrDefault() is { } usage)
                {
                    write1hAtStart = LiveApi.InputUsage(usage).CacheWrite1h;
                }

                string name = ClaudeTestClient.Name(update);
                if (!stop.IsCancellationRequested && name is "content_block_delta:thinking_delta" or "content_block_delta:text_delta")
                {
                    sinceStop.Start();
                    stop.Cancel();
                }
            }));
        sinceStop.Stop();

        LiveApi.Log($"cancel: {ex?.GetType().FullName} {sinceStop.ElapsedMilliseconds} ms after Stop; input usage at message_start: write1h={write1hAtStart}");
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(write1hAtStart > 0, "message_start carried no cache write.");
        Assert.True(sinceStop.Elapsed < TimeSpan.FromSeconds(5), $"The stream took {sinceStop.Elapsed} to end after Stop.");
        Assert.Equal(requestsBefore + 1, capture.Requests.Count);
    }

    /// <summary>
    /// Check 7: every model and level accepts the shape, writes its own corpus entry and reads it back;
    /// and entries coexist rather than replace one another, for a level change and for a model change.
    /// </summary>
    [Fact]
    public async Task EveryCombination_WritesItsOwnEntryAndReadsItBack_AndEntriesCoexist()
    {
        LiveApi.RequireEnabled();
        var capture = new CaptureHandler();
        using ClaudeChatClientProvider provider = LiveApi.CreateProvider(capture);
        string system = LiveApi.NewSystemPrompt();
        await LiveApi.AssertFixturesCacheableAsync(provider, system, Ct);

        // Collected rather than thrown: a failing combination is excluded on its own, so every one runs.
        List<string> failures = [];
        Dictionary<(string Model, EffortLevel Level), long> written = [];
        const string Sonnet = ModelIds.ClaudeSonnet5;

        async Task<ChatResponse?> Ask(string label, string model, EffortLevel level, string question)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                (ChatResponse response, _) = await LiveApi.StreamAsync(
                    provider.ForEffort(level),
                    LiveApi.Request(model, level, system, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, question)]),
                    model,
                    Ct);
                LiveApi.Record(label, model, level, response, watch.Elapsed);
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{model} {level.ToWireValue()}, {label}: {ex.GetType().Name}: {ex.Message}");
                LiveApi.Log($"{label}: {model} {level.ToWireValue()} FAILED {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        async Task WriteThenRead(string model, EffortLevel level)
        {
            string who = $"{model} {level.ToWireValue()}";
            ChatResponse? cold = await Ask("cold", model, level, Capacity);
            if (cold is null)
            {
                return;
            }

            TokenUsage coldUsage = LiveApi.Usage(cold);
            written[(model, level)] = coldUsage.CacheWrite1h;
            if (coldUsage.CacheWrite1h == 0 || coldUsage.CacheRead != 0)
            {
                failures.Add($"{who}: expected a cold 1-hour write; saw write1h={coldUsage.CacheWrite1h}, read={coldUsage.CacheRead}");
            }

            if (LiveApi.StopReason(cold) != "end_turn")
            {
                failures.Add($"{who}: stopped with {LiveApi.StopReason(cold)}, not end_turn");
            }

            // At low effort adaptive thinking may produce no thinking block; the reply must still replay.
            try
            {
                if (ReplayConverter.ToReplay(Reply(cold)).Count == 0)
                {
                    failures.Add($"{who}: the reply has nothing replayable");
                }
            }
            catch (NotSupportedException ex)
            {
                failures.Add($"{who}: the reply cannot be replayed: {ex.Message}");
            }

            ChatResponse? warm = await Ask("warm", model, level, PermitDuration);
            long warmRead = warm is null ? 0 : LiveApi.Usage(warm).CacheRead;
            if (warm is not null && warmRead != coldUsage.CacheWrite1h)
            {
                failures.Add($"{who}: expected to read the {coldUsage.CacheWrite1h}-token prefix; read {warmRead}");
            }
        }

        // Each re-read asks its own question: a repeated one would also read the previous re-read's tail.
        async Task RereadDefault(string after, string question)
        {
            ChatResponse? again = await Ask($"Opus 5 high again, after {after}", Opus, EffortLevel.High, question);
            long expected = written.GetValueOrDefault((Opus, EffortLevel.High));
            if (again is not null && LiveApi.Usage(again) is { } usage && (usage.CacheRead != expected || usage.CacheWrite1h != 0))
            {
                failures.Add(
                    $"coexistence after {after}: expected Opus 5 high to read {expected}; saw read={usage.CacheRead}, write1h={usage.CacheWrite1h}");
            }
        }

        // A -> B -> A for a level change, then for a model change; then every remaining combination.
        await WriteThenRead(Opus, EffortLevel.High);
        await WriteThenRead(Opus, EffortLevel.Low);
        await RereadDefault("a level change", TestFrequency);
        await WriteThenRead(Sonnet, EffortLevel.High);
        await RereadDefault("a model change", SensorPosition);
        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            foreach (EffortLevel level in CacheAffecting.EffortLevels)
            {
                if (!written.ContainsKey((model.Id, level)))
                {
                    await WriteThenRead(model.Id, level);
                }
            }
        }

        LiveApi.LogTotal();
        foreach (string failure in failures)
        {
            LiveApi.Log($"FAILURE {failure}");
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    private static ChatMessage Reply(ChatResponse response) =>
        response.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant) ?? new ChatMessage(ChatRole.Assistant, []);

    private static List<AIContent> Contents(ChatResponse response) => [.. Reply(response).Contents];

    /// <summary>Every citation of the reply, which must all be <c>char_location</c> citations.</summary>
    private static IEnumerable<AnswerCitation> CharLocations(ChatResponse response) =>
        Contents(response)
            .OfType<TextContent>()
            .SelectMany(text => text.Annotations ?? [])
            .Select(annotation => annotation is CitationAnnotation citation
                ? AnswerCitation.From(citation)
                : throw new InvalidOperationException($"Unexpected annotation {annotation.GetType().Name}."));
}
