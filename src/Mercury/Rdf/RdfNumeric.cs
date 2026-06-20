using System;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// The single source of truth for the xsd datatype of an RDF numeric/boolean literal and for the
/// canonical typed-literal lexical form. Shared by the Turtle / TriG streaming parsers and the SPARQL
/// <c>LiteralForm</c> canonicalizer so that the same numeric or boolean token produces a byte-identical
/// atom whether it arrives as RDF source or as a SPARQL constant (docs/divergence S1c).
/// </summary>
/// <remarks>
/// The <em>classification</em> of a numeric — whether it has a fractional point or an exponent — is
/// legitimately performed inside each grammar's parse loop (W3C Turtle [19] INTEGER / [20] DECIMAL /
/// [21] DOUBLE set the flags as characters are consumed; the SPARQL side re-scans an already-extracted
/// token). What must <em>not</em> diverge is the resulting datatype IRI and the boolean literal form —
/// those constants and the selection rule live here, so a SPARQL constant <c>30</c> always matches a
/// Turtle-ingested <c>30</c>.
/// </remarks>
internal static class RdfNumeric
{
    public const string XsdInteger = "http://www.w3.org/2001/XMLSchema#integer";
    public const string XsdDecimal = "http://www.w3.org/2001/XMLSchema#decimal";
    public const string XsdDouble = "http://www.w3.org/2001/XMLSchema#double";
    public const string XsdBoolean = "http://www.w3.org/2001/XMLSchema#boolean";

    /// <summary>The canonical typed-literal lexical forms for the two boolean values.</summary>
    public const string TrueLiteral = "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>";
    public const string FalseLiteral = "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>";

    /// <summary>
    /// The xsd datatype IRI for a numeric whose grammar production set these flags: an exponent ⇒
    /// xsd:double, else a fractional '.' ⇒ xsd:decimal, else xsd:integer.
    /// </summary>
    public static string DatatypeIri(bool hasExponent, bool hasDecimal) =>
        hasExponent ? XsdDouble : hasDecimal ? XsdDecimal : XsdInteger;

    /// <summary>The canonical typed-literal lexical form for a boolean value.</summary>
    public static string BooleanLiteral(bool value) => value ? TrueLiteral : FalseLiteral;
}
