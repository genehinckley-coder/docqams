using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Plan item 1, check 4, offline: a streamed reply, converted block by block, goes back as turn 2
/// without losing anything but its empty text blocks.
/// </summary>
public sealed class ReplayConverterTests
{
    [Fact]
    public async Task AStreamedReply_IsReplayedLosslessly_MinusItsEmptyTextBlock()
    {
        var stub = new StubHandler(_ => Sse.Response(CannedAnswer.Events));
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(
            SinglePass.FakeKey, SinglePass.Timeouts(TimeSpan.FromMinutes(1)), [stub]);
        CancellationToken ct = TestContext.Current.CancellationToken;

        MessageParam first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?");
        (Message reply, _) = await SinglePass.StreamAsync(provider.Client, Request([first]), ct);

        List<ContentBlockParam> replay = ReplayConverter.ToParams(reply.Content);
        await SinglePass.StreamAsync(
            provider.Client,
            Request([first, MessageRequestBuilder.AssistantTurn(replay), MessageRequestBuilder.FollowUpTurn(" Q2? ")]),
            ct);

        Assert.Equal(2, stub.Requests.Count);
        JsonArray turn1 = JsonNode.Parse(stub.Requests[0].Body!)!["messages"]!.AsArray();
        JsonArray turn2 = JsonNode.Parse(stub.Requests[1].Body!)!["messages"]!.AsArray();
        Assert.Equal(3, turn2.Count);

        // History is append-only: turn 2 resends the first message unchanged, except that its question now
        // carries the tail breakpoint.
        JsonNode resent = turn2[0]!.DeepClone();
        JsonObject question = resent["content"]!.AsArray()[^1]!.AsObject();
        Assert.NotNull(question["cache_control"]);
        question.Remove("cache_control");
        Assert.True(JsonNode.DeepEquals(turn1[0], resent));

        Assert.Equal("assistant", (string?)turn2[1]!["role"]);
        JsonTest.Equal(CannedAnswer.ReplayedContentJson, turn2[1]!["content"]);
        JsonTest.Equal("""{"role":"user","content":[{"type":"text","text":"Q2?"}]}""", turn2[2]);
    }

    [Fact]
    public void AReplyWithOnlyEmptyText_HasNothingToReplay()
    {
        List<ContentBlockParam> replay = ReplayConverter.ToParams([new TextBlock { Text = "", Citations = [] }]);

        Assert.Empty(replay);
    }

    [Fact]
    public void AThinkingOnlyReply_KeepsItsThinkingAndSignature()
    {
        // A max_tokens stop inside thinking leaves no text; the signature is what the API verifies.
        ContentBlockParam replayed = Assert.Single(ReplayConverter.ToParams(
            [new ThinkingBlock { Thinking = "partial", Signature = "sig" }, new TextBlock { Text = "", Citations = [] }]));

        Assert.True(replayed.TryPickThinking(out ThinkingBlockParam? thinking));
        Assert.Equal("partial", thinking.Thinking);
        Assert.Equal("sig", thinking.Signature);
    }

    [Fact]
    public void ANullDocumentTitle_IsCarriedThrough()
    {
        var citation = new TextCitation(Parse(
            """{"type":"char_location","cited_text":"x","document_index":0,"document_title":null,"start_char_index":0,"end_char_index":1}"""));

        ContentBlockParam replayed = Assert.Single(ReplayConverter.ToParams([new TextBlock { Text = "claim", Citations = [citation] }]));

        Assert.True(replayed.TryPickText(out TextBlockParam? text));
        TextCitationParam param = Assert.Single(text.Citations ?? []);
        Assert.True(param.TryPickCitationCharLocation(out CitationCharLocationParam? location));
        Assert.Null(location.DocumentTitle);
        Assert.Equal("x", location.CitedText);
        Assert.Equal(1, location.EndCharIndex);
    }

    [Fact]
    public void ACharLocationCitationMissingItsOffsets_IsRefused()
    {
        var citation = new TextCitation(Parse(
            """{"type":"char_location","cited_text":"x","document_index":0,"document_title":null,"start_char_index":0}"""));

        var ex = Assert.Throws<NotSupportedException>(() =>
            ReplayConverter.ToParams([new TextBlock { Text = "claim", Citations = [citation] }]));

        Assert.Contains("offsets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockKindTheAppNeverRequests_IsRefused_NotSkipped()
    {
        var toolUse = new ContentBlock(Parse("""{"type":"tool_use","id":"toolu_1","name":"lookup","input":{}}"""));

        var ex = Assert.Throws<NotSupportedException>(() => ReplayConverter.ToParams([toolUse]));

        Assert.Contains("tool_use", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACitationKindOtherThanCharLocation_IsRefused()
    {
        var pageCitation = new TextCitation(Parse(
            """{"type":"page_location","cited_text":"x","document_index":0,"document_title":"T","start_page_number":1,"end_page_number":2}"""));

        var ex = Assert.Throws<NotSupportedException>(() =>
            ReplayConverter.ToParams([new TextBlock { Text = "claim", Citations = [pageCitation] }]));

        Assert.Contains("page_location", ex.Message, StringComparison.Ordinal);
    }

    private static MessageCreateParams Request(IReadOnlyList<MessageParam> messages) =>
        MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, EffortLevel.High, 32_000, CacheAffecting.SystemPrompt, messages);

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
