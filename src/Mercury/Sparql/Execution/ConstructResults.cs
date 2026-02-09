using System;
using System.Buffers;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Results from CONSTRUCT query execution. Yields constructed triples from template substitution.
/// Must be disposed to return pooled resources.
/// </summary>
public ref struct ConstructResults
{
    private QueryResults _queryResults;
    private ConstructTemplate _template;
    private ReadOnlySpan<char> _source;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private bool _isEmpty;

    // Prefix mappings for expanding prefixed names in template
    private PrefixMapping[]? _prefixes;

    // Storage for expanded prefixed names
    private string? _expandedTerm;

    // Deduplication: track seen triples (CONSTRUCT returns a SET, not a bag)
    private HashSet<string>? _seenTriples;

    // Current template pattern index and whether we need a new row
    private int _templateIndex;
    private bool _needNewRow;

    // Current constructed triple
    private ConstructedTriple _current;

    // Buffer for substituted values
    private char[] _outputBuffer;
    private int _subjectStart, _subjectLen;
    private int _predicateStart, _predicateLen;
    private int _objectStart, _objectLen;

    // Row counter for generating unique blank node IDs per result row
    private int _rowCounter;

    // Storage for generated blank node IDs (one per list node in template, regenerated per row)
    private string? _generatedBnode;

    public static ConstructResults Empty()
    {
        var result = new ConstructResults();
        result._isEmpty = true;
        return result;
    }

    internal ConstructResults(QueryResults queryResults, ConstructTemplate template,
        ReadOnlySpan<char> source, Binding[] bindings, char[] stringBuffer,
        PrefixMapping[]? prefixes = null)
    {
        _queryResults = queryResults;
        _template = template;
        _source = source;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _prefixes = prefixes;
        _expandedTerm = null;
        _generatedBnode = null;
        _seenTriples = new HashSet<string>();
        _isEmpty = false;
        _templateIndex = 0;
        _needNewRow = true;
        _current = default;
        _outputBuffer = ArrayPool<char>.Shared.Rent(2048);
        _subjectStart = _subjectLen = 0;
        _predicateStart = _predicateLen = 0;
        _objectStart = _objectLen = 0;
        _rowCounter = 0;
    }

    public readonly ConstructedTriple Current => _current;

    public bool MoveNext()
    {
        if (_isEmpty || !_template.HasPatterns)
            return false;

        while (true)
        {
            // Need a new row from query results?
            if (_needNewRow)
            {
                if (!_queryResults.MoveNext())
                    return false;
                _templateIndex = 0;
                _needNewRow = false;
                _rowCounter++;  // Increment row counter for unique blank node IDs
            }

            // Process current template pattern
            if (_templateIndex < _template.PatternCount)
            {
                var pattern = _template.GetPattern(_templateIndex);
                _templateIndex++;

                // Substitute variables in the pattern
                int writePos = 0;
                var bindings = _queryResults.Current;

                // Substitute subject
                _subjectStart = writePos;
                writePos = SubstituteTerm(pattern.Subject, bindings, writePos);
                _subjectLen = writePos - _subjectStart;

                // Substitute predicate
                _predicateStart = writePos;
                writePos = SubstituteTerm(pattern.Predicate, bindings, writePos);
                _predicateLen = writePos - _predicateStart;

                // Substitute object
                _objectStart = writePos;
                writePos = SubstituteTerm(pattern.Object, bindings, writePos);
                _objectLen = writePos - _objectStart;

                // Check if any variable was unbound (skip this triple)
                if (_subjectLen == 0 || _predicateLen == 0 || _objectLen == 0)
                    continue;

                // Build triple key for deduplication
                var subject = _outputBuffer.AsSpan(_subjectStart, _subjectLen);
                var predicate = _outputBuffer.AsSpan(_predicateStart, _predicateLen);
                var obj = _outputBuffer.AsSpan(_objectStart, _objectLen);

                // CONSTRUCT returns a SET - skip duplicate triples
                var tripleKey = $"{subject.ToString()} {predicate.ToString()} {obj.ToString()}";
                if (_seenTriples != null && !_seenTriples.Add(tripleKey))
                    continue; // Already seen this triple

                _current = new ConstructedTriple(subject, predicate, obj);

                return true;
            }

            // Exhausted template patterns for this row, get next row
            _needNewRow = true;
        }
    }

    private int SubstituteTerm(Term term, BindingTable bindings, int writePos)
    {
        if (term.IsVariable)
        {
            // Look up variable binding
            var varName = _source.Slice(term.Start, term.Length);
            var idx = bindings.FindBinding(varName);
            if (idx < 0)
                return writePos; // Unbound - return same position to signal empty

            var value = bindings.GetString(idx);
            value.CopyTo(_outputBuffer.AsSpan(writePos));
            return writePos + value.Length;
        }
        // Check for synthetic terms (negative offset)
        else if (SyntheticTermHelper.IsSynthetic(term.Start))
        {
            // Check for synthetic list node blank node (-400 to -431)
            if (SyntheticTermHelper.IsListNodeOffset(term.Start))
            {
                // Generate unique blank node ID for this row + list node index
                // Format: _:genid{rowCounter}_{listNodeIndex}
                int listNodeIndex = -term.Start - 400;
                _generatedBnode = $"_:genid{_rowCounter}_{listNodeIndex}";
                _generatedBnode.AsSpan().CopyTo(_outputBuffer.AsSpan(writePos));
                return writePos + _generatedBnode.Length;
            }

            // Synthetic IRI (rdf:type, rdf:first, rdf:rest, rdf:nil, etc.)
            var syntheticIri = SyntheticTermHelper.GetSyntheticIri(term.Start);
            if (syntheticIri.Length > 0)
            {
                syntheticIri.CopyTo(_outputBuffer.AsSpan(writePos));
                return writePos + syntheticIri.Length;
            }

            return writePos; // Unknown synthetic - treat as empty
        }
        else
        {
            // Constant term - expand prefixed names and copy
            var value = _source.Slice(term.Start, term.Length);
            var expanded = ExpandPrefixedName(value);
            expanded.CopyTo(_outputBuffer.AsSpan(writePos));
            return writePos + expanded.Length;
        }
    }

    /// <summary>
    /// Expands a prefixed name to its full IRI using the prefix mappings.
    /// Also handles 'a' shorthand for rdf:type.
    /// Returns the original span if not a prefixed name or no matching prefix found.
    /// </summary>
    private ReadOnlySpan<char> ExpandPrefixedName(ReadOnlySpan<char> term)
    {
        // Skip if already a full IRI, literal, or blank node
        if (term.Length == 0 || term[0] == '<' || term[0] == '"' || term[0] == '_')
            return term;

        // Handle 'a' shorthand for rdf:type (SPARQL keyword)
        if (term.Length == 1 && term[0] == 'a')
            return SyntheticTermHelper.RdfType.AsSpan();

        // Look for colon indicating prefixed name
        var colonIdx = term.IndexOf(':');
        if (colonIdx < 0 || _prefixes == null)
            return term;

        // Include the colon in the prefix (stored prefixes include trailing colon, e.g., "ex:")
        var prefixWithColon = term.Slice(0, colonIdx + 1);
        var localPart = term.Slice(colonIdx + 1);

        // Find matching prefix in mappings
        foreach (var mapping in _prefixes)
        {
            var mappingPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);
            if (prefixWithColon.SequenceEqual(mappingPrefix))
            {
                // Found matching prefix, expand to full IRI
                // The IRI is stored with angle brackets, e.g., "<http://example.org/>"
                var iriBase = _source.Slice(mapping.IriStart, mapping.IriLength);

                // Strip angle brackets from IRI base if present, then build full IRI
                var iriContent = iriBase;
                if (iriContent.Length >= 2 && iriContent[0] == '<' && iriContent[^1] == '>')
                    iriContent = iriContent.Slice(1, iriContent.Length - 2);

                // Build full IRI: <base + localPart>
                _expandedTerm = $"<{iriContent.ToString()}{localPart.ToString()}>";
                return _expandedTerm.AsSpan();
            }
        }

        return term;
    }

    public void Dispose()
    {
        _queryResults.Dispose();
        if (_outputBuffer != null)
        {
            ArrayPool<char>.Shared.Return(_outputBuffer);
            _outputBuffer = null!;
        }
    }
}

/// <summary>
/// A constructed triple from CONSTRUCT query execution.
/// Values are valid only until next MoveNext() call.
/// </summary>
public readonly ref struct ConstructedTriple
{
    public readonly ReadOnlySpan<char> Subject;
    public readonly ReadOnlySpan<char> Predicate;
    public readonly ReadOnlySpan<char> Object;

    public ConstructedTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        Subject = subject;
        Predicate = predicate;
        Object = obj;
    }
}

/// <summary>
/// Results from DESCRIBE query execution. Yields triples describing matched resources.
/// For each resource, returns triples where the resource appears as subject or object.
/// Must be disposed to return pooled resources.
/// </summary>
