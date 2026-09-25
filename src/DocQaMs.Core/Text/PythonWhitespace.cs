using System.Text;

namespace DocQaMs.Core.Text;

/// <summary>
/// Whitespace exactly as Python's <c>str.isspace</c> defines it, which is what the prototype's
/// <c>\s</c>, <c>split()</c> and <c>strip()</c> all use.
/// </summary>
/// <remarks>
/// .NET's <see cref="char.IsWhiteSpace(char)"/> and regex <c>\s</c> differ from it: neither treats
/// U+001C to U+001F (the file, group, record and unit separators) as whitespace. Extraction must match
/// the prototype's output byte for byte, so it uses this set rather than either .NET definition.
/// Every Python whitespace character is in the BMP, so a per-<see cref="char"/> test is exact.
/// </remarks>
internal static class PythonWhitespace
{
    /// <summary>Whether <paramref name="c"/> is whitespace to Python.</summary>
    public static bool IsWhiteSpace(char c) => (int)c switch
    {
        >= 0x09 and <= 0x0D => true, // tab, line feed, vertical tab, form feed, carriage return
        >= 0x1C and <= 0x20 => true, // file, group, record and unit separators; space
        0x85 or 0xA0 or 0x1680 => true, // next line, no-break space, Ogham space mark
        >= 0x2000 and <= 0x200A => true, // en quad through hair space
        0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 => true, // line and paragraph separators, narrow and math spaces, ideographic space
        _ => false,
    };

    /// <summary>
    /// Collapses every whitespace run to one space and removes leading and trailing whitespace: Python's
    /// <c>" ".join(text.split())</c>, which equals <c>re.sub(r"\s+", " ", text).strip()</c>.
    /// </summary>
    public static string Collapse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (IsWhiteSpace(c))
            {
                // Only a run between two words becomes a space, so leading and trailing runs vanish.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Removes trailing whitespace: Python's <c>str.rstrip()</c>.</summary>
    public static string TrimEnd(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int end = text.Length;
        while (end > 0 && IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end == text.Length ? text : text[..end];
    }
}
