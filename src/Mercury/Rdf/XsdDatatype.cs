using System;

namespace SkyOmega.Mercury.Rdf;

/// <summary>The class of value an XSD datatype has, as the SPARQL operator mapping tells them apart (§17.3).</summary>
internal enum XsdKind : byte
{
    /// <summary>A datatype the operator mapping doesn't know: comparing it is RDFterm-equal only.</summary>
    Unknown,
    String,
    Boolean,
    /// <summary><c>xsd:integer</c> and every type derived from it (<c>xsd:short</c>, <c>xsd:unsignedLong</c>, …).</summary>
    Integer,
    Decimal,
    Float,
    Double,
    DateTime,
    Date,
}

/// <summary>
/// The XSD datatypes, by IRI in angle brackets as a term writes them, and the class of value each has. A datatype is
/// matched by its whole IRI: a substring test made <c>&lt;http://example/point&gt;</c> an integer type.
/// </summary>
internal static class XsdDatatype
{
    public const string Namespace = "http://www.w3.org/2001/XMLSchema#";

    public const string String = "<http://www.w3.org/2001/XMLSchema#string>";
    public const string Boolean = "<http://www.w3.org/2001/XMLSchema#boolean>";
    public const string Integer = "<http://www.w3.org/2001/XMLSchema#integer>";
    public const string Decimal = "<http://www.w3.org/2001/XMLSchema#decimal>";
    public const string Float = "<http://www.w3.org/2001/XMLSchema#float>";
    public const string Double = "<http://www.w3.org/2001/XMLSchema#double>";
    public const string DateTime = "<http://www.w3.org/2001/XMLSchema#dateTime>";
    public const string Date = "<http://www.w3.org/2001/XMLSchema#date>";
    public const string DayTimeDuration = "<http://www.w3.org/2001/XMLSchema#dayTimeDuration>";
    public const string LangString = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#langString>";

    // The XSD types, by local name, with the class of value each has. The integer types are xsd:integer's derivation
    // tree (XML Schema 2 §3.3).
    private static readonly (string Local, XsdKind Kind, string Iri)[] Types =
    [
        ("string", XsdKind.String, String),
        ("boolean", XsdKind.Boolean, Boolean),
        ("integer", XsdKind.Integer, Integer),
        ("decimal", XsdKind.Decimal, Decimal),
        ("float", XsdKind.Float, Float),
        ("double", XsdKind.Double, Double),
        ("dateTime", XsdKind.DateTime, DateTime),
        ("date", XsdKind.Date, Date),
        ("nonPositiveInteger", XsdKind.Integer, Namespaced("nonPositiveInteger")),
        ("negativeInteger", XsdKind.Integer, Namespaced("negativeInteger")),
        ("long", XsdKind.Integer, Namespaced("long")),
        ("int", XsdKind.Integer, Namespaced("int")),
        ("short", XsdKind.Integer, Namespaced("short")),
        ("byte", XsdKind.Integer, Namespaced("byte")),
        ("nonNegativeInteger", XsdKind.Integer, Namespaced("nonNegativeInteger")),
        ("unsignedLong", XsdKind.Integer, Namespaced("unsignedLong")),
        ("unsignedInt", XsdKind.Integer, Namespaced("unsignedInt")),
        ("unsignedShort", XsdKind.Integer, Namespaced("unsignedShort")),
        ("unsignedByte", XsdKind.Integer, Namespaced("unsignedByte")),
        ("positiveInteger", XsdKind.Integer, Namespaced("positiveInteger")),
    ];

    private static string Namespaced(string local) => "<" + Namespace + local + ">";

    /// <summary>The class of value of the datatype <paramref name="iri"/>, written <c>&lt;…&gt;</c>.</summary>
    public static XsdKind KindOf(ReadOnlySpan<char> iri)
    {
        if (iri.Length < Namespace.Length + 3 || iri[0] != '<' || iri[^1] != '>'
            || !iri.Slice(1, Namespace.Length).SequenceEqual(Namespace))
            return XsdKind.Unknown;
        var local = iri.Slice(1 + Namespace.Length, iri.Length - Namespace.Length - 2);
        foreach (var (name, kind, _) in Types)
        {
            if (local.SequenceEqual(name))
                return kind;
        }
        return XsdKind.Unknown;
    }

    /// <summary>
    /// The datatype a stored <c>xsd:T</c> names, as <c>&lt;…XMLSchema#T&gt;</c>; null when it isn't an <c>xsd:</c>
    /// name. Stores written before ADR-053's migration hold a datatype as this prefixed name (ADR-056 D7); every RDF
    /// parser writes the IRI.
    /// </summary>
    public static string? FromStoredPrefixedName(ReadOnlySpan<char> name)
    {
        if (name.Length < 5 || !name.StartsWith("xsd:", StringComparison.Ordinal))
            return null;
        var local = name[4..];
        foreach (var (typeName, _, iri) in Types)
        {
            if (local.SequenceEqual(typeName))
                return iri;
        }
        return Namespaced(local.ToString());
    }
}
