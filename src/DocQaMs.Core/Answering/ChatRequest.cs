using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Answering;

/// <summary>
/// A request ready for an <see cref="IChatClient"/>: the messages as sent, and the
/// options that go with them.
/// </summary>
/// <param name="Messages">
/// The conversation as sent: the caller's messages, except that the previous question is a copy carrying
/// the conversation breakpoint.
/// </param>
/// <param name="Options">The model, output limit, system prompt, reasoning, effort and top-level cache TTL.</param>
public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions Options);
