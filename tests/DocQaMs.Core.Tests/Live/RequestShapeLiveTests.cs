using System.Diagnostics;
using Anthropic;
using Anthropic.Models.Messages;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;
using DocQaMs.Core.Text;

namespace DocQaMs.Core.Tests.Live;

/// <summary>
/// Plan item 1, the request-shape spike, against the real API. Billed (about $1 in all) and skipped
/// unless <c>DOCQA_LIVE=1</c>. Each response's usage and cost is logged to
/// <c>TestResults/live.log</c>.
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
            using AnthropicClientProvider provider = LiveApi.CreateProvider(capture);

            MessageTokensCount count = await provider.Client.Messages.CountTokens(
                new MessageCountTokensParams
                {
                    Model = ModelIds.ClaudeOpus5,
                    System = CacheAffecting.SystemPrompt,
                    Messages = [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "?")],
                },
                Ct);

            // Success itself shows the file's key was used: a junk key or bearer token would be a 401.
            Assert.True(count.InputTokens > 0);
            CapturedRequest request = Assert.Single(capture.Requests);
            Assert.Equal("https", request.Uri?.Scheme);
            Assert.Equal("api.anthropic.com", request.Uri?.Host);
            Assert.True(string.Equals(key, request.ApiKey, StringComparison.Ordinal), "The key sent is not the key file's key.");
            Assert.False(request.HasAuthorization);
            Assert.DoesNotContain(request.Headers, header => header.Value.Contains("junk", StringComparison.Ordinal));
            Assert.DoesNotContain(request.Headers, header =>
                header.Name.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                || header.Name.Contains("organization", StringComparison.OrdinalIgnoreCase));
            LiveApi.Log($"credentials: count_tokens succeeded with every ANTHROPIC_* variable set to junk ({count.InputTokens:N0} tokens)");
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
    /// turn 2, the prefix cache, and usage read at <c>message_start</c>.
    /// </summary>
    [Fact]
    public async Task Opus5AtHigh_StreamsInOnePass_CachesThePrefix_AndAcceptsTheReplayedTurn()
    {
        LiveApi.RequireEnabled();
        var capture = new CaptureHandler();
        using AnthropicClientProvider provider = LiveApi.CreateProvider(capture);
        string system = LiveApi.NewSystemPrompt();
        await LiveApi.AssertFixturesCacheableAsync(provider.Client, system, Ct);
        IAnthropicClient client = provider.ForEffort(EffortLevel.High);

        // Turn 1: one HTTP request, every event forwarded, and the final message complete.
        MessageParam first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, SumpsAndPermits);
        long? write1hAtStart = null;
        int requestsBefore = capture.Requests.Count;
        var watch = Stopwatch.StartNew();
        (Message turn1, List<string> names) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(ModelIds.ClaudeOpus5, EffortLevel.High, system, [first]),
            Ct,
            streamEvent =>
            {
                if (streamEvent.TryPickStart(out RawMessageStartEvent? start))
                {
                    write1hAtStart = (start.Message.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0);
                }
            });
        LiveApi.Record("round trip, turn 1", ModelIds.ClaudeOpus5, EffortLevel.High, turn1, watch.Elapsed);

        Assert.Equal(requestsBefore + 1, capture.Requests.Count);
        Assert.Equal("message_start", names[0]);
        Assert.Equal("message_stop", names[^1]);
        Assert.Contains("content_block_delta:citations_delta", names);
        Assert.Contains("content_block_delta:signature_delta", names);

        Assert.Equal(StopReason.EndTurn, turn1.StopReason?.Value());
        Assert.Null(Record.Exception(() => Assert.Null(turn1.StopDetails)));

        long write1h = (turn1.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0);
        Assert.True(write1h > 0, "Turn 1 wrote no 1-hour cache entry.");
        Assert.Equal(write1h, write1hAtStart);
        Assert.Equal(0, turn1.Usage.CacheReadInputTokens ?? 0);

        List<ThinkingBlock> thinking = [.. turn1.Content.Select(block => block.TryPickThinking(out ThinkingBlock? t) ? t : null).OfType<ThinkingBlock>()];
        Assert.All(thinking, block => Assert.False(string.IsNullOrEmpty(block.Signature)));

        // Check 3, live: every span, converted from code points, slices exactly its cited text.
        List<CitationCharLocation> citations = [.. CharLocations(turn1)];
        Assert.NotEmpty(citations);
        string[] texts = [.. SpikeFixtures.Documents.Select(document => document.Text)];
        CodePointIndex[] indexes = [.. texts.Select(CodePointIndex.Build)];
        int afterNonBmp = 0;
        foreach (CitationCharLocation citation in citations)
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
        (Message other, _) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(ModelIds.ClaudeOpus5, EffortLevel.High, system, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, SumpTesting)]),
            Ct);
        LiveApi.Record("round trip, new conversation", ModelIds.ClaudeOpus5, EffortLevel.High, other, watch.Elapsed);
        Assert.Equal(write1h, other.Usage.CacheReadInputTokens ?? 0);
        Assert.Equal(0, (other.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0));

        // Check 4: turn 1's reply, converted, is accepted as history. Turn 2 reads the corpus plus turn 1's
        // tail, through the breakpoint on the previous question: a cited reply spans more than the 20
        // positions an automatic breakpoint looks back.
        List<ContentBlockParam> replay1 = ReplayConverter.ToParams(turn1.Content);
        MessageParam second = MessageRequestBuilder.FollowUpTurn(Exemptions);
        LiveApi.Log($"round trip: turn 1's reply has {turn1.Content.Count} content blocks, {replay1.Count} of them replayable");
        watch.Restart();
        (Message turn2, _) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(ModelIds.ClaudeOpus5, EffortLevel.High, system, [first, MessageRequestBuilder.AssistantTurn(replay1), second]),
            Ct);
        LiveApi.Record("round trip, turn 2", ModelIds.ClaudeOpus5, EffortLevel.High, turn2, watch.Elapsed);
        Assert.Equal(StopReason.EndTurn, turn2.StopReason?.Value());
        long turn1Tail = turn1.Usage.CacheCreation?.Ephemeral5mInputTokens ?? 0;
        Assert.Equal(write1h + turn1Tail, turn2.Usage.CacheReadInputTokens ?? 0);

        // Turn 3: the marker moves on to the second question, so turn 3 reads everything turn 2 wrote.
        List<ContentBlockParam> replay2 = ReplayConverter.ToParams(turn2.Content);
        watch.Restart();
        (Message turn3, _) = await LiveApi.StreamAsync(
            client,
            LiveApi.Request(
                ModelIds.ClaudeOpus5,
                EffortLevel.High,
                system,
                [
                    first,
                    MessageRequestBuilder.AssistantTurn(replay1),
                    second,
                    MessageRequestBuilder.AssistantTurn(replay2),
                    MessageRequestBuilder.FollowUpTurn(RecordsAndPermits),
                ]),
            Ct);
        LiveApi.Record("round trip, turn 3", ModelIds.ClaudeOpus5, EffortLevel.High, turn3, watch.Elapsed);
        Assert.Equal(StopReason.EndTurn, turn3.StopReason?.Value());
        long turn2Tail = turn2.Usage.CacheCreation?.Ephemeral5mInputTokens ?? 0;
        Assert.Equal((turn2.Usage.CacheReadInputTokens ?? 0) + turn2Tail, turn3.Usage.CacheReadInputTokens ?? 0);
        LiveApi.LogTotal();
    }

    /// <summary>Check 6, live: cancelling the enumeration, as Stop will, ends the answer promptly.</summary>
    [Fact]
    public async Task CancellingMidAnswer_EndsTheStreamPromptly()
    {
        LiveApi.RequireEnabled();
        var capture = new CaptureHandler();
        using AnthropicClientProvider provider = LiveApi.CreateProvider(capture);
        string system = LiveApi.NewSystemPrompt();
        await LiveApi.AssertFixturesCacheableAsync(provider.Client, system, Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int requestsBefore = capture.Requests.Count;
        long? write1hAtStart = null;
        var sinceStop = new Stopwatch();

        Exception? ex = await Record.ExceptionAsync(() => LiveApi.StreamAsync(
            provider.ForEffort(EffortLevel.High),
            LiveApi.Request(ModelIds.ClaudeOpus5, EffortLevel.High, system, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, Everything)]),
            stop.Token,
            streamEvent =>
            {
                if (streamEvent.TryPickStart(out RawMessageStartEvent? start))
                {
                    write1hAtStart = (start.Message.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0);
                }

                string name = SinglePass.Name(streamEvent);
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
        using AnthropicClientProvider provider = LiveApi.CreateProvider(capture);
        string system = LiveApi.NewSystemPrompt();
        await LiveApi.AssertFixturesCacheableAsync(provider.Client, system, Ct);

        // Collected rather than thrown: a failing combination is excluded on its own, so every one runs.
        List<string> failures = [];
        Dictionary<(string Model, EffortLevel Level), long> written = [];
        const string Opus = ModelIds.ClaudeOpus5;
        const string Sonnet = ModelIds.ClaudeSonnet5;

        async Task<Message?> Ask(string label, string model, EffortLevel level, string question)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                (Message message, _) = await LiveApi.StreamAsync(
                    provider.ForEffort(level),
                    LiveApi.Request(model, level, system, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, question)]),
                    Ct);
                LiveApi.Record(label, model, level, message, watch.Elapsed);
                return message;
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
            Message? cold = await Ask("cold", model, level, Capacity);
            if (cold is null)
            {
                return;
            }

            long write1h = (cold.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0);
            long coldRead = cold.Usage.CacheReadInputTokens ?? 0;
            written[(model, level)] = write1h;
            if (write1h == 0 || coldRead != 0)
            {
                failures.Add($"{who}: expected a cold 1-hour write; saw write1h={write1h}, read={coldRead}");
            }

            if (cold.StopReason?.Value() != StopReason.EndTurn)
            {
                failures.Add($"{who}: stopped with {cold.StopReason?.Raw()}, not end_turn");
            }

            // At low effort adaptive thinking may produce no thinking block; the reply must still replay.
            try
            {
                if (ReplayConverter.ToParams(cold.Content).Count == 0)
                {
                    failures.Add($"{who}: the reply has nothing replayable");
                }
            }
            catch (NotSupportedException ex)
            {
                failures.Add($"{who}: the reply cannot be replayed: {ex.Message}");
            }

            Message? warm = await Ask("warm", model, level, PermitDuration);
            long warmRead = warm?.Usage.CacheReadInputTokens ?? 0;
            if (warm is not null && warmRead != write1h)
            {
                failures.Add($"{who}: expected to read the {write1h}-token prefix; read {warmRead}");
            }
        }

        // Each re-read asks its own question: a repeated one would also read the previous re-read's tail.
        async Task RereadDefault(string after, string question)
        {
            Message? again = await Ask($"Opus 5 high again, after {after}", Opus, EffortLevel.High, question);
            long expected = written.GetValueOrDefault((Opus, EffortLevel.High));
            if (again is not null
                && ((again.Usage.CacheReadInputTokens ?? 0) != expected || (again.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0) != 0))
            {
                failures.Add(
                    $"coexistence after {after}: expected Opus 5 high to read {expected}; saw read={again.Usage.CacheReadInputTokens}, write1h={(again.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0)}");
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

    private static IEnumerable<CitationCharLocation> CharLocations(Message message) =>
        message.Content
            .Select(block => block.TryPickText(out TextBlock? text) ? text : null)
            .OfType<TextBlock>()
            .SelectMany(text => text.Citations ?? [])
            .Select(citation => citation.TryPickCitationCharLocation(out CitationCharLocation? location)
                ? location
                : throw new InvalidOperationException($"Unexpected citation type {citation.Type}."));
}
