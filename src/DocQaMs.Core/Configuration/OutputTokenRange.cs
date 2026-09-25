namespace DocQaMs.Core.Configuration;

/// <summary>
/// An estimated range of output tokens per answer.
/// </summary>
public sealed class OutputTokenRange
{
    /// <summary>The low end of the range.</summary>
    public int Lowest { get; set; }

    /// <summary>The high end of the range.</summary>
    public int Highest { get; set; }
}
