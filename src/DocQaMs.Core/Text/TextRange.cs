namespace DocQaMs.Core.Text;

/// <summary>A half-open range <c>[Start, End)</c> of UTF-16 offsets into one string.</summary>
/// <param name="Start">The offset of the range's first character.</param>
/// <param name="End">The offset just past the range's last character.</param>
public readonly record struct TextRange(int Start, int End)
{
    /// <summary>The number of UTF-16 units in the range.</summary>
    public int Length => End - Start;
}
