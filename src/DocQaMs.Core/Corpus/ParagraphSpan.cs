using DocQaMs.Core.Text;

namespace DocQaMs.Core.Corpus;

/// <summary>The part of one paragraph that a cited span covers.</summary>
/// <param name="Paragraph">The paragraph's index in <see cref="CorpusDocument.Paragraphs"/>.</param>
/// <param name="Range">The covered part, in UTF-16 offsets into the paragraph's own text, never empty.</param>
public readonly record struct ParagraphSpan(int Paragraph, TextRange Range);
