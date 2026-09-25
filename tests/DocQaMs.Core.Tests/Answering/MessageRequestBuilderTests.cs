using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Plan item 1, check 1, offline: the request the SDK actually sends carries every field of the
/// prototype's shape. The live half of the check is in <c>RequestShapeLiveTests</c>.
/// </summary>
public sealed class MessageRequestBuilderTests
{
    [Fact]
    public async Task AFirstQuestion_IsSentInThePrototypesShape()
    {
        MessageParam first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "  What is required?  ");

        JsonObject body = await SendAndCapture(EffortLevel.High, 32_000, [first]);

        Assert.Equal(
            ["cache_control", "max_tokens", "messages", "model", "output_config", "stream", "system", "thinking"],
            body.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ModelIds.ClaudeOpus5, (string?)body["model"]);
        Assert.Equal(32_000, (long?)body["max_tokens"]);
        Assert.Equal(CacheAffecting.SystemPrompt, (string?)body["system"]);
        Assert.True((bool?)body["stream"]);
        JsonTest.Equal(
            $$"""{"type":"{{CacheAffecting.ThinkingType}}","display":"{{CacheAffecting.ThinkingDisplay}}"}""",
            body["thinking"]);
        JsonTest.Equal("""{"effort":"high"}""", body["output_config"]);
        JsonTest.Equal(
            $$"""{"type":"ephemeral","ttl":"{{CacheAffecting.ConversationCacheTtl}}"}""",
            body["cache_control"]);

        JsonObject message = Assert.IsType<JsonObject>(Assert.Single(body["messages"]!.AsArray()));
        Assert.Equal("user", (string?)message["role"]);
        JsonArray content = message["content"]!.AsArray();
        Assert.Equal(SpikeFixtures.Documents.Count + 1, content.Count);

        for (int i = 0; i < SpikeFixtures.Documents.Count; i++)
        {
            JsonTest.Equal(ExpectedDocumentBlock(i).ToJsonString(), content[i]);
        }

        JsonTest.Equal("""{"type":"text","text":"What is required?"}""", content[^1]);
    }

    [Theory]
    [InlineData(EffortLevel.Low, "low")]
    [InlineData(EffortLevel.Medium, "medium")]
    [InlineData(EffortLevel.High, "high")]
    [InlineData(EffortLevel.XHigh, "xhigh")]
    [InlineData(EffortLevel.Max, "max")]
    public async Task EveryEffortLevel_IsSentAsItsWireString(EffortLevel level, string wire)
    {
        JsonObject body = await SendAndCapture(level, 64_000, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?")]);

        Assert.Equal(wire, (string?)body["output_config"]?["effort"]);
        Assert.Equal(level.ToWireValue(), wire);
    }

    [Fact]
    public async Task AFollowUp_MarksOnlyThePreviousQuestion_WithTheTailBreakpoint()
    {
        MessageParam first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?");
        MessageParam[] conversation =
        [
            first,
            MessageRequestBuilder.AssistantTurn([new TextBlockParam { Text = "A1." }]),
            MessageRequestBuilder.FollowUpTurn("Q2?"),
            MessageRequestBuilder.AssistantTurn([new TextBlockParam { Text = "A2." }]),
            MessageRequestBuilder.FollowUpTurn("Q3?"),
        ];
        string tail = $$"""{"type":"ephemeral","ttl":"{{CacheAffecting.ConversationCacheTtl}}"}""";

        JsonObject turn2 = await SendAndCapture(EffortLevel.High, 32_000, conversation[..3]);
        JsonObject turn3 = await SendAndCapture(EffortLevel.High, 32_000, conversation);

        // Turn 2: the first question carries the marker; the new question does not.
        JsonTest.Equal($$"""{"type":"text","text":"Q1?","cache_control":{{tail}}}""", turn2["messages"]![0]!["content"]!.AsArray()[^1]);
        JsonTest.Equal("""{"role":"user","content":[{"type":"text","text":"Q2?"}]}""", turn2["messages"]![2]);
        Assert.Equal(3, CountBreakpoints(turn2));

        // Turn 3: the marker has moved on to the second question.
        JsonTest.Equal("""{"type":"text","text":"Q1?"}""", turn3["messages"]![0]!["content"]!.AsArray()[^1]);
        JsonTest.Equal($$"""{"role":"user","content":[{"type":"text","text":"Q2?","cache_control":{{tail}}}]}""", turn3["messages"]![2]);
        JsonTest.Equal("""{"role":"user","content":[{"type":"text","text":"Q3?"}]}""", turn3["messages"]![4]);
        Assert.Equal(3, CountBreakpoints(turn3));

        // The caller's messages are never changed.
        Assert.True(first.Content.TryPickContentBlockParams(out IReadOnlyList<ContentBlockParam>? blocks));
        Assert.True(blocks[^1].TryPickText(out TextBlockParam? question));
        Assert.Null(question.CacheControl);
    }

    [Fact]
    public void APreviousQuestionSentAsABareString_IsRefused_NotConverted()
    {
        // Converting it would change the bytes of a prefix the previous request already cached.
        var bareString = new MessageParam { Role = Role.User, Content = "Q2?" };

        Assert.Throws<ArgumentException>(() => MessageRequestBuilder.Create(
            ModelIds.ClaudeOpus5,
            EffortLevel.High,
            32_000,
            CacheAffecting.SystemPrompt,
            [
                MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?"),
                MessageRequestBuilder.AssistantTurn([new TextBlockParam { Text = "A1." }]),
                bareString,
                MessageRequestBuilder.AssistantTurn([new TextBlockParam { Text = "A2." }]),
                MessageRequestBuilder.FollowUpTurn("Q3?"),
            ]));
    }

    [Fact]
    public void AnEmptyQuestion_IsRefusedBeforeAnyCall()
    {
        Assert.Throws<ArgumentException>(() => MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, " \n "));
        Assert.Throws<ArgumentException>(() => MessageRequestBuilder.FollowUpTurn(""));
    }

    [Fact]
    public void NoDocuments_OrAnEmptyDocument_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => MessageRequestBuilder.DocumentBlocks([]));
        Assert.Throws<ArgumentException>(() => MessageRequestBuilder.DocumentBlocks([new RequestDocument("T", "C", "")]));
    }

    [Fact]
    public void AModelOrLevelOutsideTheOfferedSets_IsRefused()
    {
        MessageParam first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?");

        Assert.Throws<ArgumentException>(() =>
            MessageRequestBuilder.Create("claude-haiku-4-5", EffortLevel.High, 32_000, CacheAffecting.SystemPrompt, [first]));
        Assert.Throws<ArgumentException>(() =>
            MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, (EffortLevel)99, 32_000, CacheAffecting.SystemPrompt, [first]));
        Assert.Throws<ArgumentException>(() =>
            MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, EffortLevel.High, 32_000, CacheAffecting.SystemPrompt, []));
    }

    [Fact]
    public void AnAssistantTurnWithNothingToReplay_IsRefused() =>
        Assert.Throws<ArgumentException>(() => MessageRequestBuilder.AssistantTurn([]));

    [Fact]
    public void ToSdkEffort_RejectsAnUndefinedLevel() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EffortLevel)99).ToSdkEffort());

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

    private static async Task<JsonObject> SendAndCapture(EffortLevel level, long maxTokens, IReadOnlyList<MessageParam> messages)
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(
            SinglePass.FakeKey, SinglePass.Timeouts(TimeSpan.FromMinutes(1)), [stub]);
        MessageCreateParams parameters = MessageRequestBuilder.Create(
            ModelIds.ClaudeOpus5, level, maxTokens, CacheAffecting.SystemPrompt, messages);

        await SinglePass.StreamAsync(provider.ForEffort(level), parameters, TestContext.Current.CancellationToken);

        return JsonNode.Parse(Assert.Single(stub.Requests).Body!)!.AsObject();
    }
}
