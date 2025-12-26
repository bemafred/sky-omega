// RdfTriple.cs
// RDF triple data structure

using System;

namespace SkyOmega.Rdf.Turtle;

/// <summary>
/// Represents an RDF triple (subject, predicate, object).
/// Immutable record type for zero-allocation streaming.
/// </summary>
/* TODO: Remove tthi class, it's obsolete
public readonly record struct RdfTriple
{
    public string Subject { get; init; }
    public string Predicate { get; init; }
    public string Object { get; init; }
    
    public RdfLiteral(string subject, string predicate, string @object)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        Object = @object ?? throw new ArgumentNullException(nameof(@object));
    }
    
    public override string ToString()
    {
        return $"{Subject} {Predicate} {Object} .";
    }
    
    /// <summary>
    /// Convert to N-Triples format (canonical RDF serialization)
    /// </summary>
    public string ToNTriples()
    {
        return $"{FormatTerm(Subject)} {FormatTerm(Predicate)} {FormatTerm(Object)} .";
    }
    
    private static string FormatTerm(string term)
    {
        // Simple formatting - proper implementation would handle all cases
        if (term.StartsWith("_:"))
            return term; // Blank node
        
        if (term.StartsWith('"'))
            return term; // Literal (already formatted)
        
        if (term.StartsWith("<<(") && term.EndsWith(")>>"))
            return term; // Triple term
        
        // IRI
        return $"<{term}>";
    }
}
*/
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
public readonly record struct RdfLiteral
{
    public string LexicalForm { get; init; }
    public string? DatatypeIri { get; init; }
    public string? LanguageTag { get; init; }
    public string? Direction { get; init; } // RDF 1.2 initial text direction
    
    public RdfLiteral(
        string lexicalForm, 
        string? datatypeIri = null, 
        string? languageTag = null,
        string? direction = null)
    {
        LexicalForm = lexicalForm ?? throw new ArgumentNullException(nameof(lexicalForm));
        DatatypeIri = datatypeIri;
        LanguageTag = languageTag;
        Direction = direction;
    }
    
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
