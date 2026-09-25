namespace DocQaMs.Core.Corpus;

/// <summary>
/// The corpus could not be turned into document text: a page's layout is not the one expected, or the
/// files on disk and the configured labels disagree.
/// </summary>
/// <remarks>
/// Every case means the app would otherwise answer from a partial or polluted corpus, so extraction
/// fails instead of falling back.
/// </remarks>
public sealed class CorpusExtractionException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public CorpusExtractionException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong, naming the file or folder.</param>
    public CorpusExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What went wrong, naming the file or folder.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CorpusExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
