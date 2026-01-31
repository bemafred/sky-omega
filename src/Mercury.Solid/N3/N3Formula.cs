// N3Formula.cs
// N3 formula (graph pattern) representation for Solid PATCH operations.
// Based on W3C Solid N3 Patch specification.
// https://solidproject.org/TR/n3-patch
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.N3;

/// <summary>
/// Represents an N3 formula (graph pattern) containing triple patterns.
/// Formulae are enclosed in { ... } and can contain variables.
/// </summary>
public sealed class N3Formula
{
    /// <summary>
    /// The triple patterns in this formula.
    /// </summary>
    public IReadOnlyList<N3TriplePattern> Patterns { get; }

    /// <summary>
    /// Variables used in this formula.
    /// </summary>
    public IReadOnlySet<string> Variables { get; }

    public N3Formula(IReadOnlyList<N3TriplePattern> patterns)
    {
        Patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));

        // Extract variables
        var vars = new HashSet<string>();
        foreach (var pattern in patterns)
        {
            if (pattern.Subject.IsVariable)
                vars.Add(pattern.Subject.Value);
            if (pattern.Predicate.IsVariable)
                vars.Add(pattern.Predicate.Value);
            if (pattern.Object.IsVariable)
                vars.Add(pattern.Object.Value);
        }
        Variables = vars;
    }

    /// <summary>
    /// Creates an empty formula.
    /// </summary>
    public static N3Formula Empty { get; } = new N3Formula([]);
}

/// <summary>
/// A triple pattern within an N3 formula.
/// </summary>
public readonly struct N3TriplePattern
{
    public N3Term Subject { get; }
    public N3Term Predicate { get; }
    public N3Term Object { get; }

    public N3TriplePattern(N3Term subject, N3Term predicate, N3Term obj)
    {
        Subject = subject;
        Predicate = predicate;
        Object = obj;
    }

    public override string ToString()
    {
        return $"{Subject} {Predicate} {Object} .";
    }
}

/// <summary>
/// A term in an N3 triple pattern (IRI, literal, blank node, or variable).
/// </summary>
public readonly struct N3Term
{
    /// <summary>
    /// The term value (IRI, literal value, blank node ID, or variable name).
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The term type.
    /// </summary>
    public N3TermType Type { get; }

    /// <summary>
    /// For typed literals, the datatype IRI.
    /// </summary>
    public string? Datatype { get; }

    /// <summary>
    /// For language-tagged literals, the language tag.
    /// </summary>
    public string? Language { get; }

    private N3Term(string value, N3TermType type, string? datatype = null, string? language = null)
    {
        Value = value;
        Type = type;
        Datatype = datatype;
        Language = language;
    }

    /// <summary>
    /// Creates an IRI term.
    /// </summary>
    public static N3Term Iri(string iri) => new(iri, N3TermType.Iri);

    /// <summary>
    /// Creates a literal term.
    /// </summary>
    public static N3Term Literal(string value, string? datatype = null, string? language = null)
        => new(value, N3TermType.Literal, datatype, language);

    /// <summary>
    /// Creates a blank node term.
    /// </summary>
    public static N3Term BlankNode(string id) => new(id, N3TermType.BlankNode);

    /// <summary>
    /// Creates a variable term.
    /// </summary>
    public static N3Term Variable(string name) => new(name, N3TermType.Variable);

    /// <summary>
    /// Whether this term is a variable.
    /// </summary>
    public bool IsVariable => Type == N3TermType.Variable;

    /// <summary>
    /// Whether this term is ground (not a variable).
    /// </summary>
    public bool IsGround => Type != N3TermType.Variable;

    public override string ToString()
    {
        return Type switch
        {
            N3TermType.Iri => $"<{Value}>",
            N3TermType.Literal when Language != null => $"\"{Value}\"@{Language}",
            N3TermType.Literal when Datatype != null => $"\"{Value}\"^^<{Datatype}>",
            N3TermType.Literal => $"\"{Value}\"",
            N3TermType.BlankNode => $"_:{Value}",
            N3TermType.Variable => $"?{Value}",
            _ => Value
        };
    }

    /// <summary>
    /// Converts this term to its RDF serialization form.
    /// </summary>
    public string ToRdfString()
    {
        return Type switch
        {
            N3TermType.Iri => $"<{Value}>",
            N3TermType.Literal when Language != null => $"\"{EscapeLiteral(Value)}\"@{Language}",
            N3TermType.Literal when Datatype != null => $"\"{EscapeLiteral(Value)}\"^^<{Datatype}>",
            N3TermType.Literal => $"\"{EscapeLiteral(Value)}\"",
            N3TermType.BlankNode => $"_:{Value}",
            N3TermType.Variable => throw new InvalidOperationException("Variables cannot be serialized to RDF"),
            _ => Value
        };
    }

    private static string EscapeLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}

/// <summary>
/// Types of N3 terms.
/// </summary>
public enum N3TermType
{
    /// <summary>
    /// An IRI reference.
    /// </summary>
    Iri,

    /// <summary>
    /// A literal value.
    /// </summary>
    Literal,

    /// <summary>
    /// A blank node.
    /// </summary>
    BlankNode,

    /// <summary>
    /// A variable (?name).
    /// </summary>
    Variable
}
