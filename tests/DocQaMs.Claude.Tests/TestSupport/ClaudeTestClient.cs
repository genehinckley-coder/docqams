using Anthropic.Models.Messages;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Claude.Tests.TestSupport;

/// <summary>
/// The production Claude pipeline (<see cref="ClaudeChatClientProvider"/>: the Claude middleware over the
/// SDK's adapter over a real <c>AnthropicClient</c>) wired to a test's innermost HTTP handler, and the
/// requests the app builds for it.
/// </summary>
internal static class ClaudeTestClient
{
    public const string FakeKey = "sk-ant-api03-kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";

    /// <summary>The name <see cref="Name"/> gives an update that carries no raw stream event.</summary>
    public const string NoEvent = "(no event)";

    public static Dictionary<EffortLevel, TimeSpan> Timeouts(TimeSpan each) =>
        CacheAffecting.EffortLevels.ToDictionary(level => level, _ => each);

    public static ClaudeChatClientProvider Provider(
        DelegatingHandler innermost, TimeSpan? timeout = null, ILoggerFactory? loggerFactory = null) =>
        ClaudeChatClientProvider.Create(FakeKey, Timeouts(timeout ?? TimeSpan.FromMinutes(1)), [innermost], loggerFactory);

    /// <summary>A first question against the item 1 spike fixtures.</summary>
    public static ChatMessage FirstQuestion(string question = "Q?") =>
        MessageRequestBuilder.FirstTurn(SpikeFixtures.Documents, question);

    public static ChatRequest Request(
        IReadOnlyList<ChatMessage> messages,
        EffortLevel level = EffortLevel.High,
        long maxTokens = 32_000,
        string modelId = ModelIds.ClaudeOpus5) =>
        MessageRequestBuilder.Create(modelId, level, maxTokens, CacheAffecting.SystemPrompt, messages);

    /// <summary>
    /// Enumerates one streamed response to its end, handing each update to <paramref name="onUpdate"/> as it
    /// arrives, and returns them all.
    /// </summary>
    public static async Task<List<ChatResponseUpdate>> StreamAsync(
        IChatClient client,
        ChatRequest request,
        CancellationToken cancellationToken,
        Action<ChatResponseUpdate>? onUpdate = null)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(request.Messages, request.Options, cancellationToken))
        {
            updates.Add(update);
            onUpdate?.Invoke(update);
        }

        return updates;
    }

    /// <summary>
    /// The raw stream event's <c>type</c>, with a content block delta's own type appended, or
    /// <see cref="NoEvent"/> for an update the adapter or the middleware made up.
    /// </summary>
    public static string Name(ChatResponseUpdate update)
    {
        if (update.RawRepresentation is not RawMessageStreamEvent streamEvent)
        {
            return NoEvent;
        }

        string type = streamEvent.Type.GetString() ?? "?";
        return streamEvent.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta)
            ? $"{type}:{delta.Delta.Type.GetString()}"
            : type;
    }

    /// <summary>Every content of every update, in order.</summary>
    public static List<AIContent> Contents(IEnumerable<ChatResponseUpdate> updates) =>
        [.. updates.SelectMany(update => update.Contents)];

    /// <summary>The <see cref="Core.Chat.ChatKeys.BlockIndex"/> of a response content, or null when it has none.</summary>
    public static long? BlockIndex(AIContent content) =>
        content.AdditionalProperties?.TryGetValue(Core.Chat.ChatKeys.BlockIndex, out long index) == true ? index : null;
}
