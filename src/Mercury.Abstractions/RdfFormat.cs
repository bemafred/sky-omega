// RdfFormat.cs
// Supported RDF serialization formats.
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Supported RDF serialization formats.
/// </summary>
public enum RdfFormat
{
    /// <summary>Unknown or unsupported format.</summary>
    Unknown = 0,

    /// <summary>N-Triples format (application/n-triples).</summary>
    NTriples,

    /// <summary>Turtle format (text/turtle).</summary>
    Turtle,

    /// <summary>RDF/XML format (application/rdf+xml).</summary>
    RdfXml,

    /// <summary>N-Quads format (application/n-quads).</summary>
    NQuads,

    /// <summary>TriG format (application/trig).</summary>
    TriG,

    /// <summary>JSON-LD format (application/ld+json).</summary>
    JsonLd
}
