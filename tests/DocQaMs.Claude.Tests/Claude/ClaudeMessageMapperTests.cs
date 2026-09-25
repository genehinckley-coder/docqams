using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using DocQaMs.Claude.Tests.TestSupport;
using DocQaMs.Core.Chat;
using Microsoft.Extensions.AI;

namespace DocQaMs.Claude.Tests.Claude;

/// <summary>
/// <see cref="ClaudeMessageMapper"/>: each content kind the app sends renders as the block DocQa sent, and
/// anything else is refused rather than skipped or approximated.
/// </summary>
public sealed class ClaudeMessageMapperTests
{
    [Fact]
    public void ADocument_IsAPlainTextDocumentBlock_WithCitationsEnabled()
    {
        var document = new CitableDocumentContent("Title A", "Source file: a.htm", "Text A.");

        JsonTest.Equal(
            """{"type":"document","source":{"type":"text","media_type":"text/plain","data":"Text A."},"title":"Title A","context":"Source file: a.htm","citations":{"enabled":true}}""",
            Render(document, ChatRole.User));
    }

    [Fact]
    public void ADocumentWithACacheTtl_CarriesABreakpoint()
    {
        var document = new CitableDocumentContent("T", "C", "Text.")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.CacheTtl] = "1h" },
        };

        JsonTest.Equal("""{"type":"ephemeral","ttl":"1h"}""", Render(document, ChatRole.User)["cache_control"]);
    }

    [Fact]
    public void Text_IsSentVerbatim_WithABreakpointWhenAsked()
    {
        var text = new TextContent("  Q?\n")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.CacheTtl] = "5m" },
        };

        JsonTest.Equal("""{"type":"text","text":"  Q?\n","cache_control":{"type":"ephemeral","ttl":"5m"}}""", Render(text, ChatRole.User));
        JsonTest.Equal("""{"type":"text","text":"A."}""", Render(new TextContent("A."), ChatRole.Assistant));
    }

    [Fact]
    public void CitedText_CarriesItsCharLocationCitations()
    {
        var text = new TextContent("claim") { Annotations = [Citation(title: "T"), Citation(title: null)] };

        JsonTest.Equal(
            """
            {"type":"text","text":"claim","citations":[
              {"type":"char_location","cited_text":"x","document_index":1,"document_title":"T","start_char_index":2,"end_char_index":3},
              {"type":"char_location","cited_text":"x","document_index":1,"start_char_index":2,"end_char_index":3}
            ]}
            """,
            Render(text, ChatRole.Assistant));
    }

    [Fact]
    public void Reasoning_IsAThinkingBlock_WithItsSignature()
    {
        var reasoning = new TextReasoningContent("partial") { ProtectedData = "sig" };

        JsonTest.Equal("""{"type":"thinking","thinking":"partial","signature":"sig"}""", Render(reasoning, ChatRole.Assistant));
    }

    [Fact]
    public void RedactedReasoning_IsARedactedThinkingBlock_WithItsData()
    {
        var reasoning = new TextReasoningContent("")
        {
            ProtectedData = "cmVkYWN0ZWQ=",
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.RedactedReasoning] = true },
        };

        JsonTest.Equal("""{"type":"redacted_thinking","data":"cmVkYWN0ZWQ="}""", Render(reasoning, ChatRole.Assistant));
    }

    [Fact]
    public void ReasoningWithoutASignature_OrRedactedReasoningWithoutData_IsRefused()
    {
        // The API verifies the signature; a thinking block without one would be rejected.
        Assert.Throws<NotSupportedException>(() =>
            ClaudeMessageMapper.ToContentBlockParam(new TextReasoningContent("thought"), ChatRole.Assistant));
        Assert.Throws<NotSupportedException>(() => ClaudeMessageMapper.ToContentBlockParam(
            new TextReasoningContent("")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.RedactedReasoning] = true },
            },
            ChatRole.Assistant));
    }

    [Fact]
    public void AnAlreadyRenderedBlock_IsSentAsItIs()
    {
        ContentBlockParam rendered = new TextBlockParam { Text = "raw" };

        Assert.Same(rendered.Value, ClaudeMessageMapper.ToContentBlockParam(new AIContent { RawRepresentation = rendered }, ChatRole.User).Value);
    }

    public static TheoryData<AIContent> UnsupportedContent => new()
    {
        new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
        new FunctionCallContent("call_1", "lookup"),
        new UriContent(new Uri("https://example.com/a.pdf"), "application/pdf"),
        new AIContent(),
    };

    [Theory]
    [MemberData(nameof(UnsupportedContent))]
    public void ContentTheAppNeverSends_IsRefused_NotSkipped(AIContent content)
    {
        var ex = Assert.Throws<NotSupportedException>(() => ClaudeMessageMapper.ToContentBlockParam(content, ChatRole.User));

        Assert.Contains(content.GetType().Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACitationOtherThanCharLocation_OrOneMissingItsOffsets_IsRefused()
    {
        CitationAnnotation page = Citation(title: "T");
        page.AdditionalProperties![ChatKeys.CitationType] = "page_location";
        CitationAnnotation noEnd = Citation(title: "T");
        noEnd.AdditionalProperties!.Remove(ChatKeys.EndCharIndex);
        CitationAnnotation noText = Citation(title: "T");
        noText.Snippet = null;

        var ex = Assert.Throws<NotSupportedException>(() => Render(new TextContent("c") { Annotations = [page] }, ChatRole.Assistant));
        Assert.Contains("page_location", ex.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => Render(new TextContent("c") { Annotations = [noEnd] }, ChatRole.Assistant));
        Assert.Throws<NotSupportedException>(() => Render(new TextContent("c") { Annotations = [noText] }, ChatRole.Assistant));
        Assert.Throws<NotSupportedException>(() => Render(new TextContent("c") { Annotations = [new AIAnnotation()] }, ChatRole.Assistant));
    }

    [Fact]
    public void AnUnsupportedCacheTtl_IsRefused()
    {
        var text = new TextContent("x") { AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.CacheTtl] = "2h" } };

        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeMessageMapper.ToContentBlockParam(text, ChatRole.User));

        Assert.Contains("2h", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserAndAssistantMessages_AreRendered_InOrder()
    {
        MessageParam user = ClaudeMessageMapper.ToMessageParam(new ChatMessage(ChatRole.User, [new CitableDocumentContent("T", "C", "D."), new TextContent("Q?")]));
        MessageParam assistant = ClaudeMessageMapper.ToMessageParam(new ChatMessage(ChatRole.Assistant, "A."));

        JsonTest.Equal(
            """{"role":"user","content":[{"type":"document","source":{"type":"text","media_type":"text/plain","data":"D."},"title":"T","context":"C","citations":{"enabled":true}},{"type":"text","text":"Q?"}]}""",
            ToJson(user));
        JsonTest.Equal("""{"role":"assistant","content":[{"type":"text","text":"A."}]}""", ToJson(assistant));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("tool")]
    public void ASystemOrToolMessage_IsRefused(string role)
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ClaudeMessageMapper.ToMessageParam(new ChatMessage(new ChatRole(role), "text")));

        Assert.Contains(role, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptions_RenderTheModelLimitSystemThinkingEffortAndTopLevelBreakpoint()
    {
        var options = new ChatOptions
        {
            ModelId = "claude-opus-5",
            MaxOutputTokens = 128_000,
            Instructions = "Be exact.",
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Summary },
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatKeys.Effort] = "max",
                [ChatKeys.ConversationCacheTtl] = "5m",
            },
        };

        JsonTest.Equal(
            """
            {"model":"claude-opus-5","max_tokens":128000,"system":"Be exact.","messages":[],
             "thinking":{"type":"adaptive","display":"summarized"},"output_config":{"effort":"max"},
             "cache_control":{"type":"ephemeral","ttl":"5m"}}
            """,
            ToJson(ClaudeMessageMapper.ToCreateParams(options)));
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public void WithoutTheEffortKey_TheReasoningEffortIsSent(ReasoningEffort effort, string wire)
    {
        var options = new ChatOptions { ModelId = "m", MaxOutputTokens = 10, Reasoning = new ReasoningOptions { Effort = effort } };

        Assert.Equal(wire, (string?)ToJson(ClaudeMessageMapper.ToCreateParams(options))["output_config"]?["effort"]);
    }

    [Fact]
    public void NoReasoning_OrReasoningThatIsOffOrOmitted_IsRenderedAsSuch()
    {
        JsonObject none = ToJson(ClaudeMessageMapper.ToCreateParams(new ChatOptions { ModelId = "m", MaxOutputTokens = 10 }));
        JsonObject off = ToJson(ClaudeMessageMapper.ToCreateParams(new ChatOptions
        {
            ModelId = "m",
            MaxOutputTokens = 10,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
        }));
        JsonObject omitted = ToJson(ClaudeMessageMapper.ToCreateParams(new ChatOptions
        {
            ModelId = "m",
            MaxOutputTokens = 10,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.None },
        }));

        JsonTest.Equal("""{"model":"m","max_tokens":10,"messages":[]}""", none);
        JsonTest.Equal("""{"type":"disabled"}""", off["thinking"]);
        Assert.Null(off["output_config"]);
        JsonTest.Equal("""{"type":"adaptive","display":"omitted"}""", omitted["thinking"]);
    }

    [Fact]
    public void OptionsWithoutAModelOrOutputLimit_OrWithAnUnknownEffortOrTtl_AreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => ClaudeMessageMapper.ToCreateParams(new ChatOptions { MaxOutputTokens = 10 }));
        Assert.Throws<InvalidOperationException>(() => ClaudeMessageMapper.ToCreateParams(new ChatOptions { ModelId = "m" }));
        Assert.Throws<InvalidOperationException>(() => ClaudeMessageMapper.ToCreateParams(new ChatOptions { ModelId = "m", MaxOutputTokens = 0 }));
        Assert.Throws<InvalidOperationException>(() => ClaudeMessageMapper.ToCreateParams(new ChatOptions
        {
            ModelId = "m",
            MaxOutputTokens = 10,
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.Effort] = "extreme" },
        }));
        Assert.Throws<InvalidOperationException>(() => ClaudeMessageMapper.ToCreateParams(new ChatOptions
        {
            ModelId = "m",
            MaxOutputTokens = 10,
            AdditionalProperties = new AdditionalPropertiesDictionary { [ChatKeys.ConversationCacheTtl] = "1d" },
        }));
    }

    [Fact]
    public void ToEffort_RejectsAnUnknownWireValue()
    {
        Assert.Equal(Effort.Max, ClaudeMessageMapper.ToEffort("max"));
        Assert.Throws<InvalidOperationException>(() => ClaudeMessageMapper.ToEffort("MAX"));
    }

    private static CitationAnnotation Citation(string? title) => new()
    {
        Snippet = "x",
        Title = title,
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatKeys.CitationType] = ClaudeMessageMapper.CharLocationCitationType,
            [ChatKeys.DocumentIndex] = 1L,
            [ChatKeys.StartCharIndex] = 2L,
            [ChatKeys.EndCharIndex] = 3L,
        },
    };

    private static JsonObject Render(AIContent content, ChatRole role) =>
        ToJson(ClaudeMessageMapper.ToContentBlockParam(content, role));

    private static JsonObject ToJson<T>(T value) => JsonSerializer.SerializeToNode(value)!.AsObject();

    /// <summary>The request body: the parameters serialize with their query and headers around it.</summary>
    private static JsonObject ToJson(MessageCreateParams request) => ToJson<MessageCreateParams>(request)["RawBodyData"]!.AsObject();
}
