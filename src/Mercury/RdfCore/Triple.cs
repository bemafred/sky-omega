using System;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.RdfCore;

/// <summary>
/// A transient, zero-allocation reference to an RDF triple using spans.
/// Used for streaming results and parsing.
/// </summary>
public readonly ref struct TripleRef(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
{
    public readonly ReadOnlySpan<char> Subject = subject;
    public readonly ReadOnlySpan<char> Predicate = predicate;
    public readonly ReadOnlySpan<char> Object = obj;
}

/// <summary>
/// Compact triple representation using interned IDs. 
/// Used for high-density storage in ArrayPool or MemoryMappedFiles.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Triple(long subjectId, long predicateId, long objectId)
{
    public readonly long SubjectId = subjectId;
    public readonly long PredicateId = predicateId;
    public readonly long ObjectId = objectId;
}

/// <summary>
/// Immutable record for general-purpose use.
/// </summary>
public readonly record struct RdfTriple(string Subject, string Predicate, string Object)
{
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