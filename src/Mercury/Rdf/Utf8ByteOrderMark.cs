namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// The UTF-8 byte order mark, EF BB BF (U+FEFF). It marks a text as UTF-8; it is not part of the text.
/// </summary>
/// <remarks>
/// <para>Mercury's readers skip one at the start of a document and nowhere else, and its writers never write one.
/// XML 1.0 §4.3.3 lets a UTF-8 document begin with one, and RFC 8259 §8.1 lets a JSON reader ignore one. The Turtle,
/// TriG, N-Triples, N-Quads and SPARQL grammars do not admit U+FEFF, but tools such as Windows PowerShell 5.1 write it
/// before UTF-8 text, so a document that begins with one is read as the document after it.</para>
/// <para>Each streaming parser reads at least <see cref="Length"/> bytes of its input, or all of it if shorter, before
/// it calls <see cref="LengthAt"/>, so a stream whose first read returns fewer bytes is read the same way.</para>
/// </remarks>
internal static class Utf8ByteOrderMark
{
    /// <summary>The byte order mark's length in bytes.</summary>
    internal const int Length = 3;

    /// <summary>
    /// The length of the byte order mark that begins <paramref name="documentStart"/>: <see cref="Length"/>, or 0 when
    /// the document does not begin with one.
    /// </summary>
    internal static int LengthAt(ReadOnlySpan<byte> documentStart)
        => documentStart.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? Length : 0;
}
