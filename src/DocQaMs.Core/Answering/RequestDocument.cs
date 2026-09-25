namespace DocQaMs.Core.Answering;

/// <summary>
/// One document as it is rendered into a request's document block.
/// </summary>
/// <param name="Title">The block's <c>title</c>, which appears in citations.</param>
/// <param name="Context">The block's <c>context</c>: metadata the model reads but cannot cite.</param>
/// <param name="Text">The document text. Citation offsets index into exactly this string.</param>
public sealed record RequestDocument(string Title, string Context, string Text);
