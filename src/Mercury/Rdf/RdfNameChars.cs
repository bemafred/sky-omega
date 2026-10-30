// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// The name characters of the RDF grammars: PN_CHARS_BASE, PN_CHARS_U and PN_CHARS, the same productions in
/// Turtle ([163s]–[166s]), TriG, N-Triples, N-Quads (blank node labels) and SPARQL ([164]–[166]). Each takes a Unicode
/// code point, so the supplementary planes (U+10000–U+EFFFF) are classified, not truncated; -1 (end of input) is
/// none of them.
/// </summary>
internal static class RdfNameChars
{
    /// <summary>
    /// PN_CHARS_BASE ::= [A-Z] | [a-z] | [#x00C0-#x00D6] | [#x00D8-#x00F6] | [#x00F8-#x02FF] | [#x0370-#x037D] |
    /// [#x037F-#x1FFF] | [#x200C-#x200D] | [#x2070-#x218F] | [#x2C00-#x2FEF] | [#x3001-#xD7FF] | [#xF900-#xFDCF] |
    /// [#xFDF0-#xFFFD] | [#x10000-#xEFFFF]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPnCharsBase(int codePoint) =>
        (codePoint >= 'A' && codePoint <= 'Z') ||
        (codePoint >= 'a' && codePoint <= 'z') ||
        (codePoint >= 0x00C0 && codePoint <= 0x00D6) ||
        (codePoint >= 0x00D8 && codePoint <= 0x00F6) ||
        (codePoint >= 0x00F8 && codePoint <= 0x02FF) ||
        (codePoint >= 0x0370 && codePoint <= 0x037D) ||
        (codePoint >= 0x037F && codePoint <= 0x1FFF) ||
        (codePoint >= 0x200C && codePoint <= 0x200D) ||
        (codePoint >= 0x2070 && codePoint <= 0x218F) ||
        (codePoint >= 0x2C00 && codePoint <= 0x2FEF) ||
        (codePoint >= 0x3001 && codePoint <= 0xD7FF) ||
        (codePoint >= 0xF900 && codePoint <= 0xFDCF) ||
        (codePoint >= 0xFDF0 && codePoint <= 0xFFFD) ||
        (codePoint >= 0x10000 && codePoint <= 0xEFFFF);

    /// <summary>PN_CHARS_U ::= PN_CHARS_BASE | '_'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPnCharsU(int codePoint) => IsPnCharsBase(codePoint) || codePoint == '_';

    /// <summary>PN_CHARS ::= PN_CHARS_U | '-' | [0-9] | #x00B7 | [#x0300-#x036F] | [#x203F-#x2040]</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPnChars(int codePoint) =>
        IsPnCharsU(codePoint) ||
        codePoint == '-' ||
        (codePoint >= '0' && codePoint <= '9') ||
        codePoint == 0x00B7 ||
        (codePoint >= 0x0300 && codePoint <= 0x036F) ||
        (codePoint >= 0x203F && codePoint <= 0x2040);
}
