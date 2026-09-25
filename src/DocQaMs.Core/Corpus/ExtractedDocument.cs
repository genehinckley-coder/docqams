namespace DocQaMs.Core.Corpus;

/// <summary>One corpus file's extracted text, paired with its citation label.</summary>
/// <param name="FileName">The corpus file name, such as <c>ADMINISTRATIVE CODE 41 175.htm</c>.</param>
/// <param name="Label">
/// The document block's <c>title</c>, from <see cref="Configuration.CacheAffecting.DocumentLabels"/>.
/// Cache-affecting.
/// </param>
/// <param name="Text">
/// The regulation text: paragraphs separated by one blank line, <c>\n</c> line endings, and no CR or
/// U+00A0.
/// </param>
public sealed record ExtractedDocument(string FileName, string Label, string Text);
