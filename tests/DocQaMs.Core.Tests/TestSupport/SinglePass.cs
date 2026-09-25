using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Tests.TestSupport;

/// <summary>
/// The single-pass streaming shape the answer service will use (plan item 1, check 2): one enumeration
/// of <c>CreateStreaming</c> that both hands every event to a consumer as it arrives and feeds the SDK's
/// aggregator, which then yields the final message.
/// </summary>
internal static class SinglePass
{
    public const string FakeKey = "sk-ant-api03-kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";

    public static Dictionary<EffortLevel, TimeSpan> Timeouts(TimeSpan each) =>
        CacheAffecting.EffortLevels.ToDictionary(level => level, _ => each);

    public static async Task<(Message Message, List<string> EventNames)> StreamAsync(
        IAnthropicClient client,
        MessageCreateParams parameters,
        CancellationToken cancellationToken,
        Action<RawMessageStreamEvent>? onEvent = null)
    {
        var aggregator = new MessageContentAggregator();
        var names = new List<string>();
        await foreach (RawMessageStreamEvent streamEvent in aggregator.CollectAsync(
            client.Messages.CreateStreaming(parameters, cancellationToken)))
        {
            names.Add(Name(streamEvent));
            onEvent?.Invoke(streamEvent);
        }

        return (aggregator.Message(), names);
    }

    /// <summary>The event's <c>type</c>, with a content block delta's own type appended.</summary>
    public static string Name(RawMessageStreamEvent streamEvent)
    {
        string type = streamEvent.Type.GetString() ?? "?";
        return streamEvent.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta)
            ? $"{type}:{delta.Delta.Type.GetString()}"
            : type;
    }
}
