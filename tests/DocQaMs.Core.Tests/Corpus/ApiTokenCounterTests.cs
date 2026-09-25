using System.Text;
using System.Text.Json.Nodes;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Corpus;
using DocQaMs.Core.Tests.TestSupport;

namespace DocQaMs.Core.Tests.Corpus;

/// <summary>
/// Plan item 3, offline: the production token counter sends a first question's request, and the
/// fingerprint covers exactly the document blocks the SDK sends.
/// </summary>
public sealed class ApiTokenCounterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCount_IsOfAFirstQuestion_WithTheSystemPromptAndAPlaceholder()
    {
        var stub = new StubHandler(_ => StubHandler.Json("""{"input_tokens":1234}"""));
        using AnthropicClientProvider provider = CreateProvider(stub);

        long tokens = await new ApiTokenCounter(() => provider).CountAsync(ModelIds.ClaudeSonnet5, SpikeFixtures.Documents, Ct);

        Assert.Equal(1234, tokens);
        CapturedRequest request = Assert.Single(stub.Requests);
        Assert.Equal("/v1/messages/count_tokens", request.Uri?.AbsolutePath);

        JsonObject body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal(ModelIds.ClaudeSonnet5, (string?)body["model"]);
        Assert.Equal(CacheAffecting.SystemPrompt, (string?)body["system"]);
        JsonObject message = Assert.IsType<JsonObject>(Assert.Single(body["messages"]!.AsArray()));
        Assert.Equal("user", (string?)message["role"]);
        JsonTest.Equal($$"""{"type":"text","text":"{{ApiTokenCounter.PlaceholderQuestion}}"}""", message["content"]!.AsArray()[^1]);
    }

    [Fact]
    public async Task TheFingerprintedBlocks_AreExactlyTheBlocksTheSdkSends()
    {
        // The fingerprint is written field by field, not by the SDK. If MessageRequestBuilder ever sends a
        // field the fingerprint does not cover, or renders one differently, this fails.
        var stub = new StubHandler(_ => StubHandler.Json("""{"input_tokens":1}"""));
        using AnthropicClientProvider provider = CreateProvider(stub);

        await new ApiTokenCounter(() => provider).CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, Ct);

        JsonArray content = JsonNode.Parse(Assert.Single(stub.Requests).Body!)!["messages"]![0]!["content"]!.AsArray();
        JsonArray sentBlocks = [.. content.Take(content.Count - 1).Select(block => block!.DeepClone())];
        JsonTest.Equal(Encoding.UTF8.GetString(CorpusFingerprint.CanonicalJson(SpikeFixtures.Documents)), sentBlocks);
    }

    [Fact]
    public async Task TheClient_IsObtainedWhenACountIsMade_NotBefore()
    {
        // A missing key file must surface as a failed count, which the UI can show and retry.
        var counter = new ApiTokenCounter(() => throw new FileNotFoundException("API key file not found."));

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => counter.CountAsync(ModelIds.ClaudeOpus5, SpikeFixtures.Documents, Ct));
    }

    private static AnthropicClientProvider CreateProvider(StubHandler stub) =>
        AnthropicClientProvider.Create("sk-ant-test-key", TestSettings.ValidOptions().RequestTimeouts, [stub]);
}
