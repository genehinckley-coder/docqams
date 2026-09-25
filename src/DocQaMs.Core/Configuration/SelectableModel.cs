namespace DocQaMs.Core.Configuration;

/// <summary>
/// A model the user can choose for a conversation.
/// </summary>
/// <param name="Id">The API model id, such as <c>claude-opus-5</c>.</param>
/// <param name="DisplayName">The name shown in the UI, such as <c>Claude Opus 5</c>.</param>
public sealed record SelectableModel(string Id, string DisplayName);
