using System;
using System.Buffers;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This struct is internal because it is an
/// implementation detail of CONSTRUCT query execution.</para>
/// </remarks>
internal ref struct ConstructResults
{
    private QueryResults _queryResults;
    private ConstructTemplate _template;
    private ReadOnlySpan<char> _source;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private bool _isEmpty;

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

    public static ConstructResults Empty()
    {
        var result = new ConstructResults();
        result._isEmpty = true;
        return result;
    }

    internal ConstructResults(QueryResults queryResults, ConstructTemplate template,
        ReadOnlySpan<char> source, Binding[] bindings, char[] stringBuffer)
    {
        _queryResults = queryResults;
        _template = template;
        _source = source;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _isEmpty = false;
        _templateIndex = 0;
        _needNewRow = true;
        _current = default;
        _outputBuffer = ArrayPool<char>.Shared.Rent(2048);
        _subjectStart = _subjectLen = 0;
        _predicateStart = _predicateLen = 0;
        _objectStart = _objectLen = 0;
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

                _current = new ConstructedTriple(
                    _outputBuffer.AsSpan(_subjectStart, _subjectLen),
                    _outputBuffer.AsSpan(_predicateStart, _predicateLen),
                    _outputBuffer.AsSpan(_objectStart, _objectLen));

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
        else
        {
            // Constant term - copy from source
            var value = _source.Slice(term.Start, term.Length);
            value.CopyTo(_outputBuffer.AsSpan(writePos));
            return writePos + value.Length;
        }
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
