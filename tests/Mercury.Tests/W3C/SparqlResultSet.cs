// Licensed under the MIT License.

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Represents the type of an RDF term in SPARQL results.
/// </summary>
public enum RdfTermType
{
    /// <summary>An IRI/URI reference.</summary>
    Uri,
    /// <summary>A literal value (may have datatype or language tag).</summary>
    Literal,
    /// <summary>A blank node.</summary>
    BNode,
    /// <summary>Unbound variable (no value).</summary>
    Unbound
}

/// <summary>
/// Represents a single binding value in a SPARQL result.
/// Captures the full RDF term including type, datatype, and language tag.
/// </summary>
public sealed record SparqlBinding
{
    /// <summary>The term type (URI, Literal, BNode, Unbound).</summary>
    public RdfTermType Type { get; init; }

    /// <summary>The value (IRI string, literal value, or blank node label).</summary>
    public string Value { get; init; } = "";

    /// <summary>For literals: the datatype IRI (null for plain literals).</summary>
    public string? Datatype { get; init; }

    /// <summary>For literals: the language tag (null if not language-tagged).</summary>
    public string? Language { get; init; }

    /// <summary>Creates an unbound value.</summary>
    public static SparqlBinding Unbound => new() { Type = RdfTermType.Unbound };

    /// <summary>Creates a URI binding.</summary>
    public static SparqlBinding Uri(string value) => new() { Type = RdfTermType.Uri, Value = value };

    /// <summary>Creates a blank node binding.</summary>
    public static SparqlBinding BNode(string label) => new() { Type = RdfTermType.BNode, Value = label };

    /// <summary>Creates a plain literal binding.</summary>
    public static SparqlBinding Literal(string value) => new() { Type = RdfTermType.Literal, Value = value };

    /// <summary>Creates a typed literal binding.</summary>
    public static SparqlBinding TypedLiteral(string value, string datatype) =>
        new() { Type = RdfTermType.Literal, Value = value, Datatype = datatype };

    /// <summary>Creates a language-tagged literal binding.</summary>
    public static SparqlBinding LangLiteral(string value, string language) =>
        new() { Type = RdfTermType.Literal, Value = value, Language = language };

    /// <summary>
    /// Checks if this binding is semantically equal to another, treating blank nodes as wildcards
    /// that can match any other blank node (for isomorphism checking).
    /// </summary>
    public bool MatchesIgnoringBNodeLabels(SparqlBinding other)
    {
        if (Type != other.Type)
            return false;

        // Blank nodes match any other blank node (labels are local)
        if (Type == RdfTermType.BNode)
            return true;

        if (Type == RdfTermType.Unbound)
            return true;

        // For URIs and literals, values must match
        if (Value != other.Value)
            return false;

        // For literals, datatype and language must also match
        if (Type == RdfTermType.Literal)
        {
            if (Datatype != other.Datatype)
                return false;
            if (Language != other.Language)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets a normalized string representation for comparison.
    /// </summary>
    public string ToNormalizedString()
    {
        return Type switch
        {
            RdfTermType.Uri => $"<{Value}>",
            RdfTermType.BNode => $"_:{Value}",
            RdfTermType.Literal when Language != null => $"\"{Value}\"@{Language}",
            RdfTermType.Literal when Datatype != null => $"\"{Value}\"^^<{Datatype}>",
            RdfTermType.Literal => $"\"{Value}\"",
            RdfTermType.Unbound => "UNDEF",
            _ => Value
        };
    }

    public override string ToString() => ToNormalizedString();
}

/// <summary>
/// Represents a single result row (solution) in a SPARQL result set.
/// </summary>
public sealed class SparqlResultRow
{
    private readonly Dictionary<string, SparqlBinding> _bindings = new();

    /// <summary>Gets the binding for a variable, or Unbound if not present.</summary>
    public SparqlBinding this[string variable] =>
        _bindings.TryGetValue(variable, out var binding) ? binding : SparqlBinding.Unbound;

    /// <summary>Gets all variable names bound in this row.</summary>
    public IEnumerable<string> Variables => _bindings.Keys;

    /// <summary>Gets the number of bindings in this row.</summary>
    public int Count => _bindings.Count;

    /// <summary>Sets the binding for a variable.</summary>
    public void Set(string variable, SparqlBinding binding)
    {
        _bindings[variable] = binding;
    }

    /// <summary>Checks if a variable is bound in this row.</summary>
    public bool IsBound(string variable) =>
        _bindings.TryGetValue(variable, out var b) && b.Type != RdfTermType.Unbound;

    /// <summary>Gets all bindings as key-value pairs.</summary>
    public IEnumerable<KeyValuePair<string, SparqlBinding>> GetBindings() => _bindings;

    /// <summary>
    /// Gets a hash code that treats blank node labels as equivalent.
    /// Used for initial bucketing in isomorphism checking.
    /// </summary>
    public int GetStructuralHashCode()
    {
        var hash = new HashCode();
        foreach (var (variable, binding) in _bindings.OrderBy(kv => kv.Key))
        {
            // Skip unbound bindings - they should not affect the hash
            // This ensures rows with explicit Unbound entries hash the same as rows without them
            if (binding.Type == RdfTermType.Unbound)
                continue;

            hash.Add(variable);
            hash.Add(binding.Type);
            // Don't include blank node labels in hash - they vary between implementations
            if (binding.Type != RdfTermType.BNode)
            {
                hash.Add(binding.Value);
                hash.Add(binding.Datatype);
                hash.Add(binding.Language);
            }
        }
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var parts = _bindings.Select(kv => $"?{kv.Key}={kv.Value}");
        return $"{{ {string.Join(", ", parts)} }}";
    }
}

/// <summary>
/// Represents a complete SPARQL result set (SELECT query results).
/// </summary>
public sealed class SparqlResultSet
{
    private readonly List<string> _variables = new();
    private readonly List<SparqlResultRow> _rows = new();

    /// <summary>Gets the ordered list of result variables.</summary>
    public IReadOnlyList<string> Variables => _variables;

    /// <summary>Gets all result rows.</summary>
    public IReadOnlyList<SparqlResultRow> Rows => _rows;

    /// <summary>Gets the number of result rows.</summary>
    public int Count => _rows.Count;

    /// <summary>For ASK queries: the boolean result.</summary>
    public bool? BooleanResult { get; set; }

    /// <summary>Whether this is an ASK query result (boolean) vs SELECT (bindings).</summary>
    public bool IsBoolean => BooleanResult.HasValue;

    /// <summary>Adds a variable to the result set.</summary>
    public void AddVariable(string variable)
    {
        if (!_variables.Contains(variable))
            _variables.Add(variable);
    }

    /// <summary>Adds a result row.</summary>
    public void AddRow(SparqlResultRow row)
    {
        _rows.Add(row);
    }

    /// <summary>Creates an empty result set.</summary>
    public static SparqlResultSet Empty() => new();

    /// <summary>Creates a boolean result set (for ASK queries).</summary>
    public static SparqlResultSet Boolean(bool value) => new() { BooleanResult = value };

    public override string ToString()
    {
        if (IsBoolean)
            return $"ASK: {BooleanResult}";

        return $"SELECT ({string.Join(", ", _variables.Select(v => $"?{v}"))}): {_rows.Count} rows";
    }
}

/// <summary>
/// Represents a set of RDF triples (for CONSTRUCT/DESCRIBE results).
/// </summary>
public sealed class SparqlGraphResult
{
    private readonly List<(SparqlBinding Subject, SparqlBinding Predicate, SparqlBinding Object)> _triples = new();

    /// <summary>Gets all triples in the graph.</summary>
    public IReadOnlyList<(SparqlBinding Subject, SparqlBinding Predicate, SparqlBinding Object)> Triples => _triples;

    /// <summary>Gets the number of triples.</summary>
    public int Count => _triples.Count;

    /// <summary>Adds a triple to the graph.</summary>
    public void AddTriple(SparqlBinding subject, SparqlBinding predicate, SparqlBinding obj)
    {
        _triples.Add((subject, predicate, obj));
    }

    /// <summary>Creates an empty graph result.</summary>
    public static SparqlGraphResult Empty() => new();
}
