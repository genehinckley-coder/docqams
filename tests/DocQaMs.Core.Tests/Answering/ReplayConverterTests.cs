using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Plan item 1, check 4, offline and provider-neutral: a streamed reply, converted block by block, goes back as
/// turn 2 without losing anything but its empty text blocks. That the Claude provider sends the replay as the
/// prototype's wire blocks is tested in DocQaMs.Claude.Tests.
/// </summary>
public sealed class ReplayConverterTests
{
    [Fact]
    public async Task AStreamedReply_IsReplayedLosslessly_MinusItsEmptyTextBlock()
    {
        using var harness = new AnswerHarness(CannedAnswer.Updates);
        CancellationToken ct = TestContext.Current.CancellationToken;

        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?");
        AnswerResult reply = await harness.CompleteAsync(messages: [first], cancellationToken: ct);

        List<AIContent> replay = ReplayConverter.ToReplay(reply.Reply);
        await harness.CompleteAsync(
            messages: [first, MessageRequestBuilder.AssistantTurn(replay), MessageRequestBuilder.FollowUpTurn(" Q2? ")],
            cancellationToken: ct);

        Assert.Collection(
            replay,
            content => AssertReasoning(content, CannedAnswer.Thinking, CannedAnswer.Signature, redacted: false),
            content => AssertReasoning(content, "", CannedAnswer.RedactedData, redacted: true),
            content => AssertText(content, "Sumps must hold "),
            content => AssertText(content, "a minimum of 5 gallons", CannedAnswer.Citation1, CannedAnswer.Citation2),
            content => AssertText(content, "."));

        Assert.Equal(2, harness.Client.Calls.Count);
        IReadOnlyList<ChatMessage> turn1 = harness.Client.Calls[0].Messages;
        IReadOnlyList<ChatMessage> turn2 = harness.Client.Calls[1].Messages;
        Assert.Equal(3, turn2.Count);

        // History is append-only: turn 2 resends the first message unchanged, except that its question now
        // carries the tail breakpoint.
        Assert.Equal(turn1[0].Contents.SkipLast(1), turn2[0].Contents.SkipLast(1));
        TextContent question = Assert.IsType<TextContent>(turn2[0].Contents[^1]);
        Assert.Equal("Q1?", question.Text);
        Assert.Equal("5m", question.AdditionalProperties?[ChatKeys.CacheTtl]);

        Assert.Equal(ChatRole.Assistant, turn2[1].Role);
        Assert.Equal(replay, turn2[1].Contents);
        Assert.Equal(ChatRole.User, turn2[2].Role);
        Assert.Equal("Q2?", Assert.IsType<TextContent>(Assert.Single(turn2[2].Contents)).Text);
    }

    [Fact]
    public void AReplyMessage_AndItsContents_ConvertAlike()
    {
        var reply = new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("t") { ProtectedData = "sig" }, new TextContent("claim")]);

        List<AIContent> fromMessage = ReplayConverter.ToReplay(reply);
        List<AIContent> fromContents = ReplayConverter.ToReplay(reply.Contents.AsReadOnly());

        Assert.Equal(2, fromMessage.Count);
        AssertReasoning(fromMessage[0], "t", "sig", redacted: false);
        AssertReasoning(fromContents[0], "t", "sig", redacted: false);
        AssertText(fromMessage[1], "claim");
        AssertText(fromContents[1], "claim");
    }

    [Fact]
    public void TheReplay_IsNewContents_CarryingOnlyWhatIsReplayed()
    {
        // The reply's contents carry their block indexes; the replay must not send them back, nor share them.
        var text = new TextContent("claim")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.BlockIndex] = 3L },
            RawRepresentation = "the provider's block",
        };

        AIContent replayed = Assert.Single(ReplayConverter.ToReplay([text]));

        Assert.NotSame(text, replayed);
        Assert.Null(replayed.AdditionalProperties);
        Assert.Null(replayed.RawRepresentation);
    }

    [Fact]
    public void AReplyWithOnlyEmptyText_HasNothingToReplay()
    {
        List<AIContent> replay = ReplayConverter.ToReplay([new TextContent(""), new TextContent(null)]);

        Assert.Empty(replay);
    }

    [Fact]
    public void AThinkingOnlyReply_KeepsItsThinkingAndSignature()
    {
        // A max_tokens stop inside thinking leaves no text; the signature is what the API verifies.
        AIContent replayed = Assert.Single(ReplayConverter.ToReplay(
            [new TextReasoningContent("partial") { ProtectedData = "sig" }, new TextContent("")]));

        AssertReasoning(replayed, "partial", "sig", redacted: false);
    }

    [Fact]
    public void RedactedThinking_KeepsOnlyItsDataAndMarker()
    {
        var redacted = new TextReasoningContent("ignored")
        {
            ProtectedData = "cmVkYWN0ZWQ=",
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.RedactedReasoning] = true, [ChatKeys.BlockIndex] = 1L },
        };

        AIContent replayed = Assert.Single(ReplayConverter.ToReplay([redacted]));

        AssertReasoning(replayed, "", "cmVkYWN0ZWQ=", redacted: true);
        Assert.Single(replayed.AdditionalProperties!);
    }

    [Fact]
    public void ANullDocumentTitle_IsCarriedThrough()
    {
        CitationAnnotation citation = NeutralUpdates.Citation(0, 1, "x", title: null);

        AIContent replayed = Assert.Single(ReplayConverter.ToReplay([new TextContent("claim") { Annotations = [citation] }]));

        AssertText(replayed, "claim", citation);
        Assert.Null(Assert.IsType<CitationAnnotation>(Assert.Single(((TextContent)replayed).Annotations!)).Title);
    }

    [Fact]
    public void ACharLocationCitationMissingItsOffsets_IsRefused()
    {
        CitationAnnotation citation = NeutralUpdates.RawCitation("char_location", "x", documentIndex: 0, start: 0);

        var ex = Assert.Throws<NotSupportedException>(() =>
            ReplayConverter.ToReplay([new TextContent("claim") { Annotations = [citation] }]));

        Assert.Contains("offsets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContentKindTheAppNeverRequests_IsRefused_NotSkipped()
    {
        var toolCall = new FunctionCallContent("toolu_1", "lookup");

        var ex = Assert.Throws<NotSupportedException>(() => ReplayConverter.ToReplay([toolCall]));

        Assert.Contains(nameof(FunctionCallContent), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACitationKindOtherThanCharLocation_IsRefused()
    {
        CitationAnnotation pageCitation = NeutralUpdates.RawCitation("page_location", "x", title: "T");

        var ex = Assert.Throws<NotSupportedException>(() =>
            ReplayConverter.ToReplay([new TextContent("claim") { Annotations = [pageCitation] }]));

        Assert.Contains("page_location", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnnotationThatIsNotACitation_IsRefused()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ReplayConverter.ToReplay([new TextContent("claim") { Annotations = [new AIAnnotation()] }]));

        Assert.Contains(nameof(AIAnnotation), ex.Message, StringComparison.Ordinal);
    }

    private static void AssertReasoning(AIContent content, string text, string protectedData, bool redacted)
    {
        TextReasoningContent reasoning = Assert.IsType<TextReasoningContent>(content);
        Assert.Equal(text, reasoning.Text);
        Assert.Equal(protectedData, reasoning.ProtectedData);
        Assert.Equal(redacted, ReplayConverter.IsRedacted(reasoning));
        if (!redacted)
        {
            Assert.Null(reasoning.AdditionalProperties);
        }
    }

    /// <summary>Asserts a replayed text and its citations, field by field, under the neutral keys only.</summary>
    private static void AssertText(AIContent content, string text, params CitationAnnotation[] citations)
    {
        TextContent replayed = Assert.IsType<TextContent>(content);
        Assert.Equal(text, replayed.Text);
        Assert.Null(replayed.AdditionalProperties);
        if (citations.Length == 0)
        {
            Assert.Null(replayed.Annotations);
            return;
        }

        Assert.Equal(citations.Length, replayed.Annotations?.Count);
        for (int i = 0; i < citations.Length; i++)
        {
            CitationAnnotation actual = Assert.IsType<CitationAnnotation>(replayed.Annotations![i]);
            Assert.Equal(citations[i].Snippet, actual.Snippet);
            Assert.Equal(citations[i].Title, actual.Title);
            Assert.Equal(citations[i].AdditionalProperties!.ToDictionary(), actual.AdditionalProperties!.ToDictionary());
        }
    }
}
