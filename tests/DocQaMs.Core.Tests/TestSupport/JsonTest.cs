using System.Text.Json.Nodes;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>Structural JSON comparison for captured request bodies.</summary>
internal static class JsonTest
{
    /// <summary>
    /// Asserts that <paramref name="actual"/>, with null-valued properties removed, equals
    /// <paramref name="expectedJson"/>. Property order is ignored; array order is not.
    /// </summary>
    public static void Equal(string expectedJson, JsonNode? actual)
    {
        JsonNode? expected = JsonNode.Parse(expectedJson);
        JsonNode? stripped = WithoutNulls(actual);
        Assert.True(
            JsonNode.DeepEquals(expected, stripped),
            $"JSON differs.\nExpected: {expected?.ToJsonString()}\nActual:   {stripped?.ToJsonString()}");
    }

    public static JsonNode? WithoutNulls(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .Where(property => property.Value is not null)
            .Select(property => KeyValuePair.Create(property.Key, WithoutNulls(property.Value)))),
        JsonArray array => new JsonArray([.. array.Select(WithoutNulls)]),
        _ => node?.DeepClone(),
    };
}
