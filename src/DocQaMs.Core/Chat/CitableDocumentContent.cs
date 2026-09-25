using Microsoft.Extensions.AI;

namespace DocQaMs.Core.Chat;

/// <summary>
/// A plain-text document the model may quote from, with citations enabled.
/// </summary>
/// <remarks>
/// Microsoft.Extensions.AI has no content type for a citable document, so the app defines one; the
/// provider's chat client renders it as the provider's document block. Citations of it come back as
/// <see cref="CitationAnnotation"/>s whose <see cref="ChatKeys.DocumentIndex"/> is the document's position
/// among the request's documents, and whose offsets index into exactly <see cref="Text"/>.
/// </remarks>
public sealed class CitableDocumentContent : AIContent
{
    /// <summary>Creates a citable document.</summary>
    /// <param name="title">The document's title, which appears in citations.</param>
    /// <param name="context">Metadata the model reads but cannot cite.</param>
    /// <param name="text">The document text. It must not be empty.</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    public CitableDocumentContent(string title, string context, string text)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(text);
        Title = title;
        Context = context;
        Text = text;
    }

    /// <summary>The document's title, which appears in citations.</summary>
    public string Title { get; }

    /// <summary>Metadata the model reads but cannot cite.</summary>
    public string Context { get; }

    /// <summary>The document text. Citation offsets index into exactly this string, in code points.</summary>
    public string Text { get; }
}
