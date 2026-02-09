using System;
using System.Text;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// Helper methods for Unicode code point operations using System.Text.Rune.
/// SPARQL string functions operate on Unicode code points (characters),
/// not UTF-16 code units. This matters for characters outside the BMP
/// (code points > U+FFFF) which are encoded as surrogate pairs in UTF-16.
/// </summary>
internal static class UnicodeHelper
{
    /// <summary>
    /// Counts the number of Unicode code points in a span.
    /// Surrogate pairs count as one code point.
    /// </summary>
    public static int GetCodePointCount(ReadOnlySpan<char> text)
    {
        int count = 0;
        var enumerator = text.EnumerateRunes();
        while (enumerator.MoveNext())
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Gets a substring by code point indices (1-based, per SPARQL spec).
    /// </summary>
    /// <param name="text">The source text</param>
    /// <param name="startCodePoint">1-based start index in code points</param>
    /// <param name="lengthCodePoints">Number of code points to extract, or -1 for remainder</param>
    /// <returns>The substring as a new string, or empty if out of bounds</returns>
    public static string SubstringByCodePoints(ReadOnlySpan<char> text, int startCodePoint, int lengthCodePoints)
    {
        if (startCodePoint < 1) startCodePoint = 1;

        var result = new StringBuilder();
        int currentCodePoint = 0;
        int codePointsCollected = 0;
        bool collecting = false;

        // Allocate buffer outside loop to avoid CA2014 warning
        Span<char> chars = stackalloc char[2];

        var enumerator = text.EnumerateRunes();
        while (enumerator.MoveNext())
        {
            currentCodePoint++;
            var rune = enumerator.Current;

            // Start collecting at the specified position
            if (currentCodePoint == startCodePoint)
            {
                collecting = true;
            }

            if (collecting)
            {
                // Append the rune (handles surrogate pairs automatically)
                int charsWritten = rune.EncodeToUtf16(chars);
                result.Append(chars[..charsWritten]);

                codePointsCollected++;

                // Check if we have enough code points
                if (lengthCodePoints >= 0 && codePointsCollected >= lengthCodePoints)
                {
                    break;
                }
            }
        }

        return result.ToString();
    }
}
