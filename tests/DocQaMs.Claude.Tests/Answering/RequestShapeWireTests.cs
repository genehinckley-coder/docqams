using System.Text.Json;
using System.Text.Json.Nodes;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;

namespace DocQaMs.Claude.Tests.Answering;

/// <summary>
/// Plan item 1, check 1, offline: the request the SDK actually sends, after
/// <see cref="MessageRequestBuilder"/>, the Claude middleware and the SDK's adapter, carries every field of
/// the prototype's shape. The live half of the check is in <c>RequestShapeLiveTests</c>.
/// </summary>
public sealed class RequestShapeWireTests
{
    private static readonly string Tail = $$"""{"type":"ephemeral","ttl":"{{CacheAffecting.ConversationCacheTtl}}"}""";

    [Fact]
    public async Task AFirstQuestion_IsSentInThePrototypesShape()
    {
        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "  What is required?  ");

        JsonObject body = await SendAndCapture(EffortLevel.High, 32_000, [first]);

        Assert.Equal(
            ["cache_control", "max_tokens", "messages", "model", "output_config", "stream", "system", "thinking"],
            body.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ModelIds.ClaudeOpus5, (string?)body["model"]);
        Assert.Equal(32_000, (long?)body["max_tokens"]);

        // A plain string, not a text block: it renders first in the cached prefix.
        Assert.Equal(JsonValueKind.String, body["system"]!.GetValueKind());
        Assert.Equal(CacheAffecting.SystemPrompt, (string?)body["system"]);
        Assert.True((bool?)body["stream"]);
        JsonTest.Equal(
            $$"""{"type":"{{CacheAffecting.ThinkingType}}","display":"{{CacheAffecting.ThinkingDisplay}}"}""",
            body["thinking"]);
        JsonTest.Equal("""{"effort":"high"}""", body["output_config"]);
        JsonTest.Equal(Tail, body["cache_control"]);

        JsonObject message = Assert.IsType<JsonObject>(Assert.Single(body["messages"]!.AsArray()));
        Assert.Equal("user", (string?)message["role"]);
        JsonArray content = message["content"]!.AsArray();
        Assert.Equal(SpikeFixtures.Documents.Count + 1, content.Count);

        for (int i = 0; i < SpikeFixtures.Documents.Count; i++)
        {
            JsonTest.Equal(ExpectedDocumentBlock(i).ToJsonString(), content[i]);
        }

        JsonTest.Equal("""{"type":"text","text":"What is required?"}""", content[^1]);
        Assert.Equal(2, CountBreakpoints(body));
    }

    [Theory]
    [InlineData(EffortLevel.Low, "low", 32_000)]
    [InlineData(EffortLevel.Medium, "medium", 32_000)]
    [InlineData(EffortLevel.High, "high", 32_000)]
    [InlineData(EffortLevel.XHigh, "xhigh", 64_000)]
    [InlineData(EffortLevel.Max, "max", 128_000)]
    public async Task EveryEffortLevel_IsSentAsItsWireString(EffortLevel level, string wire, long maxTokens)
    {
        JsonObject body = await SendAndCapture(level, maxTokens, [ClaudeTestClient.FirstQuestion()]);

        // ReasoningEffort has no max; the effort key carries it.
        Assert.Equal(wire, (string?)body["output_config"]?["effort"]);
        Assert.Equal(level.ToWireValue(), wire);
        Assert.Equal(maxTokens, (long?)body["max_tokens"]);
        JsonTest.Equal("""{"type":"adaptive","display":"summarized"}""", body["thinking"]);
    }

    [Fact]
    public async Task AFollowUp_MarksOnlyThePreviousQuestion_WithTheTailBreakpoint()
    {
        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?");
        ChatMessage[] conversation =
        [
            first,
            MessageRequestBuilder.AssistantTurn([new TextContent("A1.")]),
            MessageRequestBuilder.FollowUpTurn("Q2?"),
            MessageRequestBuilder.AssistantTurn([new TextContent("A2.")]),
            MessageRequestBuilder.FollowUpTurn("Q3?"),
        ];

        JsonObject turn2 = await SendAndCapture(EffortLevel.High, 32_000, conversation[..3]);
        JsonObject turn3 = await SendAndCapture(EffortLevel.High, 32_000, conversation);

        // Turn 2: the first question carries the marker; the new question does not.
        JsonTest.Equal($$"""{"type":"text","text":"Q1?","cache_control":{{Tail}}}""", turn2["messages"]![0]!["content"]!.AsArray()[^1]);
        JsonTest.Equal("""{"role":"assistant","content":[{"type":"text","text":"A1."}]}""", turn2["messages"]![1]);
        JsonTest.Equal("""{"role":"user","content":[{"type":"text","text":"Q2?"}]}""", turn2["messages"]![2]);
        Assert.Equal(3, CountBreakpoints(turn2));

        // Turn 3: the marker has moved on to the second question.
        JsonTest.Equal("""{"type":"text","text":"Q1?"}""", turn3["messages"]![0]!["content"]!.AsArray()[^1]);
        JsonTest.Equal($$"""{"role":"user","content":[{"type":"text","text":"Q2?","cache_control":{{Tail}}}]}""", turn3["messages"]![2]);
        JsonTest.Equal("""{"role":"user","content":[{"type":"text","text":"Q3?"}]}""", turn3["messages"]![4]);
        Assert.Equal(3, CountBreakpoints(turn3));

        // The caller's messages are never changed.
        Assert.Null(first.Contents[^1].AdditionalProperties);
    }

    [Fact]
    public async Task AReplayedAssistantTurn_IsSentWithItsSignatureRedactedDataAndCitations()
    {
        List<AIContent> replay =
        [
            new TextReasoningContent("I think...") { ProtectedData = "SIG-abc" },
            new TextReasoningContent("")
            {
                ProtectedData = "REDACTED",
                AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.RedactedReasoning] = true },
            },
            new TextContent("Intro. "),
            new TextContent(" Sumps hold 5 gallons.\n")
            {
                Annotations =
                [
                    Citation("minimum capacity of", 1, "Title B", 10, 29),
                    Citation("untitled", 0, null, 3, 11),
                ],
            },
        ];
        ChatMessage[] conversation =
        [
            MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?"),
            MessageRequestBuilder.AssistantTurn(replay),
            MessageRequestBuilder.FollowUpTurn("Q2?"),
        ];

        JsonObject body = await SendAndCapture(EffortLevel.Max, 128_000, conversation);

        // The text is sent verbatim, whitespace included, so the cached conversation prefix matches; a
        // null title is left out, as the API sent it.
        JsonTest.Equal(
            """
            {"role":"assistant","content":[
              {"type":"thinking","thinking":"I think...","signature":"SIG-abc"},
              {"type":"redacted_thinking","data":"REDACTED"},
              {"type":"text","text":"Intro. "},
              {"type":"text","text":" Sumps hold 5 gallons.\n","citations":[
                {"type":"char_location","cited_text":"minimum capacity of","document_index":1,"document_title":"Title B","start_char_index":10,"end_char_index":29},
                {"type":"char_location","cited_text":"untitled","document_index":0,"start_char_index":3,"end_char_index":11}
              ]}
            ]}
            """,
            body["messages"]![1]);
        JsonTest.Equal("""{"effort":"max"}""", body["output_config"]);
    }

    [Fact]
    public async Task TheCallersOptions_AreNotChanged()
    {
        ChatRequest request = ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]);
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        await ClaudeTestClient.StreamAsync(provider.ForEffort(EffortLevel.High), request, TestContext.Current.CancellationToken);
        await ClaudeTestClient.StreamAsync(provider.ForEffort(EffortLevel.High), request, TestContext.Current.CancellationToken);

        // The middleware clears the instructions and sets its factory on a copy.
        Assert.Equal(CacheAffecting.SystemPrompt, request.Options.Instructions);
        Assert.Null(request.Options.RawRepresentationFactory);
        Assert.Equal(stub.Requests[0].Body, stub.Requests[1].Body);
    }

    [Fact]
    public async Task ASystemMessage_IsRefused_AndNothingIsSent()
    {
        // Folding it into the system prompt would silently change the cached prefix.
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);
        ChatRequest request = ClaudeTestClient.Request([ClaudeTestClient.FirstQuestion()]);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => ClaudeTestClient.StreamAsync(
            provider.ForEffort(EffortLevel.High),
            request with { Messages = [new ChatMessage(ChatRole.System, "Be brief."), .. request.Messages] },
            TestContext.Current.CancellationToken));

        Assert.Contains("Instructions", ex.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    private static CitationAnnotation Citation(string citedText, long documentIndex, string? title, long start, long end) => new()
    {
        Snippet = citedText,
        Title = title,
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatKeys.CitationType] = "char_location",
            [ChatKeys.DocumentIndex] = documentIndex,
            [ChatKeys.StartCharIndex] = start,
            [ChatKeys.EndCharIndex] = end,
        },
    };

    private static int CountBreakpoints(JsonNode body) =>
        body.ToJsonString().Split("\"cache_control\"", StringSplitOptions.None).Length - 1;

    private static JsonObject ExpectedDocumentBlock(int index)
    {
        RequestDocument document = SpikeFixtures.Documents[index];
        var block = new JsonObject
        {
            ["type"] = "document",
            ["source"] = new JsonObject { ["type"] = "text", ["media_type"] = "text/plain", ["data"] = document.Text },
            ["title"] = document.Title,
            ["context"] = document.Context,
            ["citations"] = new JsonObject { ["enabled"] = true },
        };

        // Only the last block closes the corpus prefix.
        if (index == SpikeFixtures.Documents.Count - 1)
        {
            block["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = CacheAffecting.CorpusCacheTtl };
        }

        return block;
    }

    private static async Task<JsonObject> SendAndCapture(EffortLevel level, long maxTokens, IReadOnlyList<ChatMessage> messages)
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using ClaudeChatClientProvider provider = ClaudeTestClient.Provider(stub);

        await ClaudeTestClient.StreamAsync(
            provider.ForEffort(level), ClaudeTestClient.Request(messages, level, maxTokens), TestContext.Current.CancellationToken);

        return JsonNode.Parse(Assert.Single(stub.Requests).Body!)!.AsObject();
    }
}
