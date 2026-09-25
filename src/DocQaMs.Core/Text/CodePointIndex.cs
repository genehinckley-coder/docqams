namespace DocQaMs.Core.Text;

/// <summary>
/// Converts code-point offsets into one string to UTF-16 offsets, so that a citation's
/// <c>char_location</c> span can be sliced from a .NET string.
/// </summary>
/// <remarks>
/// <para>
/// The Messages API's <c>start_char_index</c> and <c>end_char_index</c> count code points, as Python's
/// <c>str</c> does; the prototype's request-shape spike confirmed this live. A .NET string counts UTF-16
/// units, and a character outside the Basic Multilingual Plane takes two of them, so without this
/// conversion every span after such a character would be off by one.
/// </para>
/// <para>
/// Only a well-formed surrogate pair counts as one code point. A lone surrogate counts as one, as it
/// does in Python.
/// </para>
/// </remarks>
public sealed class CodePointIndex
{
    // The UTF-16 offset of every code-point boundary: Length + 1 entries, so that the exclusive end
    // offset `Length` converts too. Null when the text has no surrogate pair and the map is the identity,
    // which is the case for the whole current corpus.
    private readonly int[]? _utf16Offsets;

    private CodePointIndex(int length, int[]? utf16Offsets)
    {
        Length = length;
        _utf16Offsets = utf16Offsets;
    }

    /// <summary>The text's length in code points.</summary>
    public int Length { get; }

    /// <summary>Whether code-point and UTF-16 offsets coincide, because the text has no surrogate pair.</summary>
    public bool IsIdentity => _utf16Offsets is null;

    /// <summary>
    /// Builds the index for <paramref name="text"/>.
    /// </summary>
    /// <param name="text">The exact text that was sent to the API.</param>
    /// <returns>The index.</returns>
    public static CodePointIndex Build(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int pairs = 0;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (char.IsSurrogatePair(text[i], text[i + 1]))
            {
                pairs++;
                i++;
            }
        }

        if (pairs == 0)
        {
            return new CodePointIndex(text.Length, null);
        }

        int[] offsets = new int[text.Length - pairs + 1];
        int codePoint = 0;
        for (int i = 0; i < text.Length; codePoint++)
        {
            offsets[codePoint] = i;
            i += char.IsSurrogatePair(text, i) ? 2 : 1;
        }

        offsets[codePoint] = text.Length;
        return new CodePointIndex(codePoint, offsets);
    }

    /// <summary>
    /// Converts a code-point offset to a UTF-16 offset.
    /// </summary>
    /// <param name="codePointOffset">An offset from 0 to <see cref="Length"/>, inclusive.</param>
    /// <returns>The UTF-16 offset of the same position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="codePointOffset"/> is negative or greater than <see cref="Length"/>. Check bounds
    /// against <see cref="Length"/>, not <see cref="string.Length"/>, before converting.
    /// </exception>
    public int ToUtf16(int codePointOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(codePointOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(codePointOffset, Length);
        return _utf16Offsets is null ? codePointOffset : _utf16Offsets[codePointOffset];
    }

    /// <summary>
    /// Converts a UTF-16 offset, such as one <see cref="string.IndexOf(string, StringComparison)"/> returns,
    /// to a code-point offset: the unit citations use, and every displayed character position uses.
    /// </summary>
    /// <param name="utf16Offset">An offset from 0 to the text's UTF-16 length, inclusive.</param>
    /// <returns>
    /// The code-point offset of the same position. An offset between the two halves of a surrogate pair
    /// returns the pair's own code point.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="utf16Offset"/> is negative or greater than the text's UTF-16 length.
    /// </exception>
    public int ToCodePoint(int utf16Offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(utf16Offset);
        if (_utf16Offsets is null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(utf16Offset, Length);
            return utf16Offset;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(utf16Offset, _utf16Offsets[Length]);
        int found = Array.BinarySearch(_utf16Offsets, utf16Offset);

        // Not a boundary: the offset falls inside a pair, so it belongs to the code point that starts before it.
        return found >= 0 ? found : ~found - 1;
    }
}
