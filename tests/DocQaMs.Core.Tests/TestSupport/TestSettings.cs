using DocQaMs.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>A complete, valid <c>DocQaMs</c> configuration section, as flat configuration keys.</summary>
internal static class TestSettings
{
    public static Dictionary<string, string?> Valid()
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DocQaMs:CorpusDirectory"] = "corpus",
            ["DocQaMs:ApiKeyPath"] = "key.txt",
            ["DocQaMs:StateFilePath"] = "state.json",
            ["DocQaMs:TokenCeiling"] = "400000",
            ["DocQaMs:ContextWindow"] = "1000000",
            ["DocQaMs:ContextWarnFraction"] = "0.7",
            ["DocQaMs:CacheReadToleranceFraction"] = "0.01",
            ["DocQaMs:MinCitationLength"] = "8",
            ["DocQaMs:ThinCoverageFraction"] = "0.5",
        };

        foreach (EffortLevel level in CacheAffecting.EffortLevels)
        {
            settings[$"DocQaMs:MaxTokens:{level}"] = "32000";
            settings[$"DocQaMs:RequestTimeouts:{level}"] = "00:20:00";
            settings[$"DocQaMs:EstimatedOutputTokens:{level}:Lowest"] = "1000";
            settings[$"DocQaMs:EstimatedOutputTokens:{level}:Highest"] = "5000";
        }

        foreach (SelectableModel model in CacheAffecting.SelectableModels)
        {
            settings[$"DocQaMs:Prices:{model.Id}:InputPerMTok"] = "5";
            settings[$"DocQaMs:Prices:{model.Id}:CacheWrite5mPerMTok"] = "6.25";
            settings[$"DocQaMs:Prices:{model.Id}:CacheWrite1hPerMTok"] = "10";
            settings[$"DocQaMs:Prices:{model.Id}:CacheReadPerMTok"] = "0.5";
            settings[$"DocQaMs:Prices:{model.Id}:OutputPerMTok"] = "25";
        }

        return settings;
    }

    public static IConfiguration ToConfiguration(IEnumerable<KeyValuePair<string, string?>> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    public static DocQaOptions ValidOptions() => Bind(ToConfiguration(Valid()));

    public static DocQaOptions Bind(IConfiguration configuration) =>
        configuration.GetSection(DocQaOptions.SectionName).Get<DocQaOptions>()
        ?? throw new InvalidOperationException("The DocQaMs section is missing.");
}
