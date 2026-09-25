using System.Text.Json.Nodes;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// <c>tests/golden/blocks.json</c>: the prototype's assembled document blocks, each text replaced by its
/// length and SHA-256, and the token count the prototype recorded for exactly those blocks.
/// </summary>
internal static class BlocksGolden
{
    private static readonly Lazy<JsonObject> Golden =
        new(() => JsonNode.Parse(File.ReadAllText(RepoPaths.Combine("tests", "golden", "blocks.json")))!.AsObject());

    public static JsonArray Blocks => Golden.Value["blocks"]!.AsArray();

    /// <summary>The prototype's <c>count_tokens</c> total: system prompt, blocks and a placeholder question.</summary>
    public static long PrototypeTokens => (long)Golden.Value["prototype_tokens"]!;

    public static string PrototypeTokensModel => (string)Golden.Value["prototype_tokens_model"]!;
}
