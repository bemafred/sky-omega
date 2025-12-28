// RdfTriple.cs
// RDF triple data structure

using System;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// Term type enumeration for RDF terms
/// </summary>
public enum RdfTermType
{
    IRI,
    Literal,
    BlankNode,
    TripleTerm
}

/// <summary>
/// RDF literal with lexical form, datatype, and optional language tag
/// </summary>
public readonly record struct RdfLiteral(
    string LexicalForm,
    string? DatatypeIri = null,
    string? LanguageTag = null,
    string? Direction = null)
{
    public string LexicalForm { get; init; } = LexicalForm ?? throw new ArgumentNullException(nameof(LexicalForm));

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(LanguageTag))
        {
            if (!string.IsNullOrEmpty(Direction))
                return $"\"{LexicalForm}\"@{LanguageTag}--{Direction}";
            return $"\"{LexicalForm}\"@{LanguageTag}";
        }
        
        if (!string.IsNullOrEmpty(DatatypeIri))
            return $"\"{LexicalForm}\"^^<{DatatypeIri}>";
        
        // Default to xsd:string
        return $"\"{LexicalForm}\"^^<http://www.w3.org/2001/XMLSchema#string>";
    }
}

/// <summary>
/// Parser statistics for monitoring zero-GC behavior
/// </summary>
public readonly record struct ParserStatistics
{
    public long TriplesParsed { get; init; }
    public long BytesProcessed { get; init; }
    public long LinesProcessed { get; init; }
    public TimeSpan ParseTime { get; init; }
    public int PeakBufferSize { get; init; }
    
    public double TriplesPerSecond => 
        ParseTime.TotalSeconds > 0 
            ? TriplesParsed / ParseTime.TotalSeconds 
            : 0;
    
    public double MegabytesPerSecond =>
        ParseTime.TotalSeconds > 0
            ? (BytesProcessed / 1024.0 / 1024.0) / ParseTime.TotalSeconds
            : 0;
}
