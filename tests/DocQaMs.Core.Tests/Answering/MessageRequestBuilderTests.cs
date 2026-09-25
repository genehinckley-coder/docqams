using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Plan item 1, check 1, offline and provider-neutral: the request carries every field of the prototype's shape,
/// as Microsoft.Extensions.AI contents, options and <see cref="ChatKeys"/>. That the Claude provider renders it
/// as the prototype's wire request is tested in DocQaMs.Claude.Tests; the live half of the check is there too.
/// </summary>
public sealed class MessageRequestBuilderTests
{
    [Fact]
    public void AFirstQuestion_IsBuiltInThePrototypesShape()
    {
        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "  What is required?  ");

        ChatRequest request = Create(EffortLevel.High, 32_000, [first]);

        ChatMessage message = Assert.Single(request.Messages);
        Assert.Same(first, message);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal(SpikeFixtures.Documents.Count + 1, message.Contents.Count);

        for (int i = 0; i < SpikeFixtures.Documents.Count; i++)
        {
            RequestDocument expected = SpikeFixtures.Documents[i];
            CitableDocumentContent document = Assert.IsType<CitableDocumentContent>(message.Contents[i]);
            Assert.Equal(expected.Title, document.Title);
            Assert.Equal(expected.Context, document.Context);
            Assert.Equal(expected.Text, document.Text);

            // Only the last document closes the corpus prefix.
            if (i == SpikeFixtures.Documents.Count - 1)
            {
                Assert.Equal(CacheAffecting.CorpusCacheTtl, Assert.Single(document.AdditionalProperties!, p => p.Key == ChatKeys.CacheTtl).Value);
                Assert.Single(document.AdditionalProperties!);
            }
            else
            {
                Assert.Null(document.AdditionalProperties);
            }
        }

        TextContent question = Assert.IsType<TextContent>(message.Contents[^1]);
        Assert.Equal("What is required?", question.Text);
        Assert.Null(question.AdditionalProperties);
        Assert.Null(question.Annotations);
    }

    [Fact]
    public void TheOptions_CarryTheModel_OutputLimit_SystemPrompt_Reasoning_AndCacheTtl()
    {
        ChatRequest request = Create(EffortLevel.High, 32_000, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?")]);

        ChatOptions options = request.Options;
        Assert.Equal(ModelIds.ClaudeOpus5, options.ModelId);
        Assert.Equal(32_000, options.MaxOutputTokens);
        Assert.Equal(CacheAffecting.SystemPrompt, options.Instructions);
        Assert.Equal("summarized", CacheAffecting.ThinkingDisplay);
        Assert.Equal(ReasoningOutput.Summary, options.Reasoning?.Output);
        Assert.Equal(ReasoningEffort.High, options.Reasoning?.Effort);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                [ChatKeys.Effort] = "high",
                [ChatKeys.ConversationCacheTtl] = CacheAffecting.ConversationCacheTtl,
            },
            options.AdditionalProperties!.ToDictionary());
        Assert.Equal("5m", CacheAffecting.ConversationCacheTtl);

        // Nothing a provider would read as a tool, a temperature or a response format.
        Assert.Null(options.Tools);
        Assert.Null(options.Temperature);
        Assert.Null(options.ResponseFormat);
    }

    [Theory]
    [InlineData(EffortLevel.Low, "low", "Low")]
    [InlineData(EffortLevel.Medium, "medium", "Medium")]
    [InlineData(EffortLevel.High, "high", "High")]
    [InlineData(EffortLevel.XHigh, "xhigh", "ExtraHigh")]
    [InlineData(EffortLevel.Max, "max", "ExtraHigh")]
    public void EveryEffortLevel_IsCarriedAsItsWireString_AndTheNearestReasoningEffort(EffortLevel level, string wire, string reasoning)
    {
        ChatRequest request = Create(level, 64_000, [MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?")]);

        // ReasoningEffort has no max; the key is authoritative.
        Assert.Equal(wire, request.Options.AdditionalProperties?[ChatKeys.Effort]);
        Assert.Equal(level.ToWireValue(), wire);
        Assert.Equal(Enum.Parse<ReasoningEffort>(reasoning), request.Options.Reasoning?.Effort);
    }

    [Fact]
    public void AFollowUp_MarksOnlyThePreviousQuestion_WithTheTailBreakpoint()
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

        ChatRequest turn2 = Create(EffortLevel.High, 32_000, conversation[..3]);
        ChatRequest turn3 = Create(EffortLevel.High, 32_000, conversation);

        // Turn 2: the first question carries the marker; the new question does not.
        Assert.Equal("Q1?", Assert.IsType<TextContent>(turn2.Messages[0].Contents[^1]).Text);
        Assert.Equal(CacheAffecting.ConversationCacheTtl, TtlOf(turn2.Messages[0].Contents[^1]));
        Assert.Null(TtlOf(turn2.Messages[2].Contents[^1]));
        Assert.Equal(3, CountBreakpoints(turn2));

        // Turn 3: the marker has moved on to the second question.
        Assert.Null(TtlOf(turn3.Messages[0].Contents[^1]));
        Assert.Equal("Q2?", Assert.IsType<TextContent>(turn3.Messages[2].Contents[^1]).Text);
        Assert.Equal(CacheAffecting.ConversationCacheTtl, TtlOf(turn3.Messages[2].Contents[^1]));
        Assert.Null(TtlOf(turn3.Messages[4].Contents[^1]));
        Assert.Equal(3, CountBreakpoints(turn3));

        // The caller's messages are never changed; only the marked message is a copy, keeping its documents.
        Assert.DoesNotContain(
            conversation.SelectMany(message => message.Contents), content => TtlOf(content) == CacheAffecting.ConversationCacheTtl);
        Assert.Equal(conversation.Length, turn3.Messages.Count);
        Assert.NotSame(conversation[2], turn3.Messages[2]);
        Assert.Equal(ChatRole.User, turn3.Messages[2].Role);
        Assert.Same(conversation[0], turn3.Messages[0]);
        Assert.Same(conversation[1], turn3.Messages[1]);
        Assert.Same(conversation[3], turn3.Messages[3]);
        Assert.Same(conversation[4], turn3.Messages[4]);
        Assert.NotSame(conversation[0], turn2.Messages[0]);
        Assert.Equal(first.Contents.Take(SpikeFixtures.Documents.Count), turn2.Messages[0].Contents.Take(SpikeFixtures.Documents.Count));
    }

    [Fact]
    public void TheTailBreakpoint_KeepsTheQuestionsOtherProperties()
    {
        ChatMessage question = MessageRequestBuilder.FollowUpTurn("Q2?");
        question.Contents[0].AdditionalProperties = new AdditionalPropertiesDictionary { ["other"] = "kept" };
        ChatMessage[] conversation =
        [
            MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?"),
            MessageRequestBuilder.AssistantTurn([new TextContent("A1.")]),
            question,
            MessageRequestBuilder.AssistantTurn([new TextContent("A2.")]),
            MessageRequestBuilder.FollowUpTurn("Q3?"),
        ];

        ChatRequest request = Create(EffortLevel.High, 32_000, conversation);

        AdditionalPropertiesDictionary sent = request.Messages[2].Contents[^1].AdditionalProperties!;
        Assert.Equal("kept", sent["other"]);
        Assert.Equal(CacheAffecting.ConversationCacheTtl, sent[ChatKeys.CacheTtl]);
        Assert.False(question.Contents[0].AdditionalProperties!.ContainsKey(ChatKeys.CacheTtl));
    }

    [Fact]
    public void APreviousQuestionNotEndingInText_IsRefused_NotConverted()
    {
        // Converting it would change the bytes of a prefix the previous request already cached.
        var notAQuestion = new ChatMessage(ChatRole.User, [new CitableDocumentContent("T", "C", "Text.")]);

        var ex = Assert.Throws<ArgumentException>(() => Create(EffortLevel.High, 32_000, FollowUpAfter(notAQuestion)));

        Assert.StartsWith(
            "Message 2 should be a question built by FirstTurn or FollowUpTurn, ending in a text content.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APreviousMessageThatIsNotAQuestion_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Create(EffortLevel.High, 32_000, FollowUpAfter(new ChatMessage(ChatRole.Assistant, "Not a question."))));

        Assert.StartsWith("Message 2 should be the previous question, a user message.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyQuestion_IsRefusedBeforeAnyCall()
    {
        var first = Assert.Throws<ArgumentException>(() => MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, " \n "));
        var followUp = Assert.Throws<ArgumentException>(() => MessageRequestBuilder.FollowUpTurn(""));

        Assert.StartsWith("The question is empty.", first.Message, StringComparison.Ordinal);
        Assert.StartsWith("The question is empty.", followUp.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDocuments_OrAnEmptyDocument_AreRefused()
    {
        var none = Assert.Throws<ArgumentException>(() => MessageRequestBuilder.DocumentBlocks([]));
        var empty = Assert.Throws<ArgumentException>(() => MessageRequestBuilder.DocumentBlocks([new RequestDocument("T", "C", "")]));

        Assert.StartsWith("At least one document is required.", none.Message, StringComparison.Ordinal);
        Assert.StartsWith("Document 0 (\"T\") has no text.", empty.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelOrLevelOutsideTheOfferedSets_IsRefused()
    {
        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?");

        var model = Assert.Throws<ArgumentException>(() =>
            MessageRequestBuilder.Create("claude-haiku-4-5", EffortLevel.High, 32_000, CacheAffecting.SystemPrompt, [first]));
        var level = Assert.Throws<ArgumentException>(() =>
            MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, (EffortLevel)99, 32_000, CacheAffecting.SystemPrompt, [first]));
        var messages = Assert.Throws<ArgumentException>(() =>
            MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, EffortLevel.High, 32_000, CacheAffecting.SystemPrompt, []));

        Assert.StartsWith("\"claude-haiku-4-5\" is not a selectable model.", model.Message, StringComparison.Ordinal);
        Assert.StartsWith("99 is not a selectable effort level.", level.Message, StringComparison.Ordinal);
        Assert.StartsWith("A request needs at least one message.", messages.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutputLimitOutsideWhatCanBeSent_IsRefused()
    {
        ChatMessage first = MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q?");

        Assert.Throws<ArgumentOutOfRangeException>(() => Create(EffortLevel.High, 0, [first]));
        Assert.Throws<OverflowException>(() => Create(EffortLevel.High, (long)int.MaxValue + 1, [first]));
        Assert.Throws<ArgumentException>(() =>
            MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, EffortLevel.High, 32_000, " ", [first]));
    }

    [Fact]
    public void AnAssistantTurnWithNothingToReplay_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => MessageRequestBuilder.AssistantTurn([]));

        Assert.StartsWith("An assistant turn needs at least one replayable block.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssistantTurn_CarriesTheReplayAsGiven()
    {
        List<AIContent> replay = [new TextReasoningContent("Thinking.") { ProtectedData = "sig" }, new TextContent("A.")];

        ChatMessage turn = MessageRequestBuilder.AssistantTurn(replay);

        Assert.Equal(ChatRole.Assistant, turn.Role);
        Assert.Equal(replay, turn.Contents);
    }

    private static ChatRequest Create(EffortLevel level, long maxTokens, IReadOnlyList<ChatMessage> messages) =>
        MessageRequestBuilder.Create(ModelIds.ClaudeOpus5, level, maxTokens, CacheAffecting.SystemPrompt, messages);

    /// <summary>A three-turn conversation whose previous question is <paramref name="previous"/>.</summary>
    private static ChatMessage[] FollowUpAfter(ChatMessage previous) =>
    [
        MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, "Q1?"),
        MessageRequestBuilder.AssistantTurn([new TextContent("A1.")]),
        previous,
        MessageRequestBuilder.AssistantTurn([new TextContent("A2.")]),
        MessageRequestBuilder.FollowUpTurn("Q3?"),
    ];

    private static string? TtlOf(AIContent content) =>
        content.AdditionalProperties is { } properties && properties.TryGetValue(ChatKeys.CacheTtl, out string? ttl) ? ttl : null;

    /// <summary>The breakpoints a request carries: one per content with a cache TTL, plus the top-level one.</summary>
    private static int CountBreakpoints(ChatRequest request) =>
        request.Messages.SelectMany(message => message.Contents).Count(content => TtlOf(content) is not null)
        + (request.Options.AdditionalProperties?.ContainsKey(ChatKeys.ConversationCacheTtl) == true ? 1 : 0);
}
