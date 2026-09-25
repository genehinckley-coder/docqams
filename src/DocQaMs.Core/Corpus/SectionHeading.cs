namespace DocQaMs.Core.Corpus;

/// <summary>A section's heading in the body of a document, for section navigation.</summary>
/// <param name="Paragraph">The heading paragraph's index in <see cref="CorpusDocument.Paragraphs"/>.</param>
/// <param name="Number">The section number, such as <c>175.410</c> or <c>175.APPENDIX A</c>.</param>
/// <param name="Heading">The whole heading, such as <c>Section 175.410 Sumps</c>.</param>
public sealed record SectionHeading(int Paragraph, string Number, string Heading);
