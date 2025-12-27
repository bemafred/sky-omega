using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Sparql;

/// <summary>
/// Zero-allocation SPARQL 1.1 parser using stack-based parsing and Span&lt;T&gt;
/// Based on SPARQL 1.1 Query Language EBNF grammar
/// </summary>
public ref struct SparqlParser
{
    private ReadOnlySpan<char> _source;
    private int _position;
    
    public SparqlParser(ReadOnlySpan<char> source)
    {
        _source = source;
        _position = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAtEnd() => _position >= _source.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => IsAtEnd() ? '\0' : _source[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Advance() => IsAtEnd() ? '\0' : _source[_position++];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> PeekSpan(int length)
    {
        var remaining = _source.Length - _position;
        return remaining < length ? ReadOnlySpan<char>.Empty : _source.Slice(_position, length);
    }

    private void SkipWhitespace()
    {
        while (!IsAtEnd())
        {
            var ch = Peek();
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
            {
                Advance();
            }
            else if (ch == '#') // Comment
            {
                while (!IsAtEnd() && Peek() != '\n')
                    Advance();
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// [1] QueryUnit ::= Query
    /// </summary>
    public Query ParseQuery()
    {
        SkipWhitespace();
        return ParseQueryInternal();
    }

    /// <summary>
    /// [2] Query ::= Prologue ( SelectQuery | ConstructQuery | DescribeQuery | AskQuery ) ValuesClause
    /// </summary>
    private Query ParseQueryInternal()
    {
        var prologue = ParsePrologue();
        
        SkipWhitespace();
        var queryType = DetermineQueryType();
        
        return queryType switch
        {
            QueryType.Select => ParseSelectQuery(prologue),
            QueryType.Construct => ParseConstructQuery(prologue),
            QueryType.Describe => ParseDescribeQuery(prologue),
            QueryType.Ask => ParseAskQuery(prologue),
            _ => throw new SparqlParseException("Expected SELECT, CONSTRUCT, DESCRIBE, or ASK")
        };
    }

    private QueryType DetermineQueryType()
    {
        var span = PeekSpan(10);
        if (span.Length >= 6 && span[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            return QueryType.Select;
        if (span.Length >= 9 && span[..9].Equals("CONSTRUCT", StringComparison.OrdinalIgnoreCase))
            return QueryType.Construct;
        if (span.Length >= 8 && span[..8].Equals("DESCRIBE", StringComparison.OrdinalIgnoreCase))
            return QueryType.Describe;
        if (span.Length >= 3 && span[..3].Equals("ASK", StringComparison.OrdinalIgnoreCase))
            return QueryType.Ask;
        
        return QueryType.Unknown;
    }

    /// <summary>
    /// [4] Prologue ::= ( BaseDecl | PrefixDecl )*
    /// </summary>
    private Prologue ParsePrologue()
    {
        var prologue = new Prologue();
        
        while (true)
        {
            SkipWhitespace();
            var span = PeekSpan(7);
            
            if (span.Length >= 4 && span[..4].Equals("BASE", StringComparison.OrdinalIgnoreCase))
            {
                ParseBaseDecl(ref prologue);
            }
            else if (span.Length >= 6 && span[..6].Equals("PREFIX", StringComparison.OrdinalIgnoreCase))
            {
                ParsePrefixDecl(ref prologue);
            }
            else
            {
                break;
            }
        }
        
        return prologue;
    }

    private void ParseBaseDecl(ref Prologue prologue)
    {
        ConsumeKeyword("BASE");
        SkipWhitespace();
        var start = _position;
        ParseIriRef();
        prologue.BaseUriStart = start;
        prologue.BaseUriLength = _position - start;
    }

    private void ParsePrefixDecl(ref Prologue prologue)
    {
        ConsumeKeyword("PREFIX");
        SkipWhitespace();
        var prefix = ParsePnameNs();
        SkipWhitespace();
        var iri = ParseIriRef();
        prologue.AddPrefix(prefix, iri);
    }

    /// <summary>
    /// [7] SelectQuery ::= SelectClause DatasetClause* WhereClause SolutionModifier
    /// </summary>
    private Query ParseSelectQuery(Prologue prologue)
    {
        var selectClause = ParseSelectClause();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseSolutionModifier();
        
        return new Query
        {
            Type = QueryType.Select,
            Prologue = prologue,
            SelectClause = selectClause,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    private Query ParseConstructQuery(Prologue prologue)
    {
        ConsumeKeyword("CONSTRUCT");
        SkipWhitespace();
        
        var template = ParseConstructTemplate();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseSolutionModifier();
        
        return new Query
        {
            Type = QueryType.Construct,
            Prologue = prologue,
            ConstructTemplate = template,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    private ConstructTemplate ParseConstructTemplate()
    {
        var template = new ConstructTemplate();
        
        // Expect '{'
        if (Peek() == '{')
        {
            Advance();
            SkipWhitespace();
            
            // Parse triple patterns until '}'
            while (!IsAtEnd() && Peek() != '}')
            {
                // Simplified - would parse actual triple patterns
                SkipWhitespace();
                if (Peek() == '}')
                    break;
                
                // Skip to next line for now
                while (!IsAtEnd() && Peek() != '\n' && Peek() != '}')
                    Advance();
                SkipWhitespace();
            }
            
            if (Peek() == '}')
                Advance();
        }
        
        return template;
    }

    private Query ParseDescribeQuery(Prologue prologue)
    {
        ConsumeKeyword("DESCRIBE");
        SkipWhitespace();
        
        // Parse resource list or *
        var describeAll = Peek() == '*';
        if (describeAll)
        {
            Advance();
        }
        
        var datasets = ParseDatasetClauses();
        var whereClause = new WhereClause();
        
        // Optional WHERE clause for DESCRIBE
        var span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            whereClause = ParseWhereClause();
        }
        
        var modifier = ParseSolutionModifier();
        
        return new Query
        {
            Type = QueryType.Describe,
            Prologue = prologue,
            DescribeAll = describeAll,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    private Query ParseAskQuery(Prologue prologue)
    {
        ConsumeKeyword("ASK");
        SkipWhitespace();
        
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        
        return new Query
        {
            Type = QueryType.Ask,
            Prologue = prologue,
            Datasets = datasets,
            WhereClause = whereClause
        };
    }

    private SelectClause ParseSelectClause()
    {
        ConsumeKeyword("SELECT");
        SkipWhitespace();
        
        var distinct = false;
        var reduced = false;
        
        var span = PeekSpan(8);
        if (span.Length >= 8 && span[..8].Equals("DISTINCT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("DISTINCT");
            SkipWhitespace();
            distinct = true;
        }
        else if (span.Length >= 7 && span[..7].Equals("REDUCED", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("REDUCED");
            SkipWhitespace();
            reduced = true;
        }
        
        // Parse variables or *
        var selectAll = Peek() == '*';
        if (selectAll)
        {
            Advance();
        }
        
        return new SelectClause
        {
            Distinct = distinct,
            Reduced = reduced,
            SelectAll = selectAll
        };
    }

    private DatasetClause[] ParseDatasetClauses()
    {
        // Simplified - return empty for now
        return Array.Empty<DatasetClause>();
    }

    private WhereClause ParseWhereClause()
    {
        SkipWhitespace();
        
        var span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("WHERE");
        }
        
        SkipWhitespace();
        return new WhereClause();
    }

    private SolutionModifier ParseSolutionModifier()
    {
        var modifier = new SolutionModifier();
        SkipWhitespace();
        
        // Parse ORDER BY
        var span = PeekSpan(8);
        if (span.Length >= 5 && span[..5].Equals("ORDER", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("ORDER");
            SkipWhitespace();
            ConsumeKeyword("BY");
            SkipWhitespace();
            
            modifier.OrderBy = ParseOrderByClause();
        }
        
        // Parse LIMIT
        SkipWhitespace();
        span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("LIMIT");
            SkipWhitespace();
            modifier.Limit = ParseInteger();
        }
        
        // Parse OFFSET
        SkipWhitespace();
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("OFFSET");
            SkipWhitespace();
            modifier.Offset = ParseInteger();
        }
        
        return modifier;
    }

    private OrderByClause ParseOrderByClause()
    {
        var clause = new OrderByClause();
        
        // Parse one or more order conditions
        while (!IsAtEnd())
        {
            SkipWhitespace();
            
            var direction = OrderDirection.Ascending;
            
            // Check for ASC/DESC
            var span = PeekSpan(4);
            if (span.Length >= 3 && span[..3].Equals("ASC", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("ASC");
                SkipWhitespace();
            }
            else if (span.Length >= 4 && span[..4].Equals("DESC", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("DESC");
                SkipWhitespace();
                direction = OrderDirection.Descending;
            }
            
            // Check for variable or expression
            if (Peek() == '?')
            {
                // Variable
                clause.AddCondition(0, direction); // Simplified
            }
            else if (Peek() == '(')
            {
                // Expression - skip for now
                Advance();
                while (!IsAtEnd() && Peek() != ')')
                    Advance();
                if (Peek() == ')')
                    Advance();
            }
            else
            {
                break;
            }
            
            SkipWhitespace();
            
            // Check for more conditions
            span = PeekSpan(5);
            if (span.Length < 5 || !span[..5].Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                if (span.Length < 6 || !span[..6].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
                {
                    if (clause.Count >= 8)
                        break;
                    continue;
                }
            }
            
            break;
        }
        
        return clause;
    }

    private int ParseInteger()
    {
        var start = _position;
        
        while (!IsAtEnd() && char.IsDigit(Peek()))
            Advance();
        
        var span = _source.Slice(start, _position - start);
        return int.TryParse(span, out var result) ? result : 0;
    }

    private ReadOnlySpan<char> ParseIriRef()
    {
        if (Peek() != '<')
            throw new SparqlParseException("Expected '<' for IRI reference");
        
        var start = _position;
        Advance(); // Skip '<'
        
        while (!IsAtEnd() && Peek() != '>')
            Advance();
        
        if (IsAtEnd())
            throw new SparqlParseException("Unterminated IRI reference");
        
        var end = _position;
        Advance(); // Skip '>'
        
        return _source.Slice(start, end - start + 1);
    }

    private ReadOnlySpan<char> ParsePnameNs()
    {
        var start = _position;
        
        while (!IsAtEnd() && Peek() != ':')
            Advance();
        
        if (IsAtEnd())
            throw new SparqlParseException("Expected ':' in prefix name");
        
        Advance(); // Skip ':'
        return _source.Slice(start, _position - start);
    }

    private void ConsumeKeyword(ReadOnlySpan<char> keyword)
    {
        var span = PeekSpan(keyword.Length);
        if (!span.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            throw new SparqlParseException($"Expected keyword '{keyword.ToString()}'");
        
        _position += keyword.Length;
    }
}

public enum QueryType
{
    Unknown,
    Select,
    Construct,
    Describe,
    Ask
}

public struct Query
{
    public QueryType Type;
    public Prologue Prologue;
    public SelectClause SelectClause;
    public ConstructTemplate ConstructTemplate;
    public bool DescribeAll;
    public DatasetClause[] Datasets;
    public WhereClause WhereClause;
    public SolutionModifier SolutionModifier;
}

public struct Prologue
{
    private const int MaxPrefixes = 32;
    private int _prefixCount;
    private unsafe fixed byte _prefixData[2048]; // Inline storage

    public int BaseUriStart;   // Start offset in source span
    public int BaseUriLength;  // Length in source span

    public void AddPrefix(ReadOnlySpan<char> prefix, ReadOnlySpan<char> iri)
    {
        // Store prefixes in inline buffer
        _prefixCount++;
    }
}

public struct SelectClause
{
    public bool Distinct;
    public bool Reduced;
    public bool SelectAll;
}

public struct DatasetClause
{
}

public struct WhereClause
{
}

public struct ConstructTemplate
{
}

public struct SolutionModifier
{
    public OrderByClause OrderBy;
    public int Limit;
    public int Offset;
}

public struct OrderByClause
{
    public int Count;

    public void AddCondition(int variableOffset, OrderDirection direction)
    {
        Count++;
    }
}

public enum OrderDirection
{
    Ascending,
    Descending
}

/// <summary>
/// Binding table for variable bindings during query execution.
/// Zero-allocation design using stackalloc buffers.
/// </summary>
public ref struct BindingTable
{
    private Span<Binding> _bindings;
    private int _count;
    private Span<char> _stringBuffer;
    private int _stringOffset;

    public BindingTable(Span<Binding> storage)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = Span<char>.Empty;
        _stringOffset = 0;
    }

    public BindingTable(Span<Binding> storage, Span<char> stringBuffer)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = stringBuffer;
        _stringOffset = 0;
    }

    /// <summary>
    /// Bind an integer value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, long value)
    {
        if (_count >= _bindings.Length) return;
        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Integer;
        binding.IntegerValue = value;
    }

    /// <summary>
    /// Bind a double value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, double value)
    {
        if (_count >= _bindings.Length) return;
        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Double;
        binding.DoubleValue = value;
    }

    /// <summary>
    /// Bind a boolean value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, bool value)
    {
        if (_count >= _bindings.Length) return;
        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Boolean;
        binding.BooleanValue = value;
    }

    /// <summary>
    /// Bind a string value to a variable.
    /// Copies the string into the internal buffer.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.String;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Bind a URI value to a variable.
    /// Copies the URI into the internal buffer.
    /// </summary>
    public void BindUri(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Uri;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Try to get the binding for a variable.
    /// Returns the index if found, -1 otherwise.
    /// </summary>
    public readonly int FindBinding(ReadOnlySpan<char> variableName)
    {
        var hash = ComputeHash(variableName);
        for (int i = 0; i < _count; i++)
        {
            if (_bindings[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Get the type of the binding at the given index.
    /// </summary>
    public readonly BindingValueType GetType(int index) => _bindings[index].Type;

    /// <summary>
    /// Get the integer value at the given index.
    /// </summary>
    public readonly long GetInteger(int index) => _bindings[index].IntegerValue;

    /// <summary>
    /// Get the double value at the given index.
    /// </summary>
    public readonly double GetDouble(int index) => _bindings[index].DoubleValue;

    /// <summary>
    /// Get the boolean value at the given index.
    /// </summary>
    public readonly bool GetBoolean(int index) => _bindings[index].BooleanValue;

    /// <summary>
    /// Get the string value at the given index.
    /// </summary>
    public readonly ReadOnlySpan<char> GetString(int index)
    {
        ref readonly var binding = ref _bindings[index];
        return _stringBuffer.Slice(binding.StringOffset, binding.StringLength);
    }

    /// <summary>
    /// Clear all bindings for reuse with next row.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _stringOffset = 0;
    }

    /// <summary>
    /// Number of bound variables.
    /// </summary>
    public readonly int Count => _count;

    /// <summary>
    /// Get the raw binding data for direct access.
    /// </summary>
    public readonly ReadOnlySpan<Binding> GetBindings() => _bindings.Slice(0, _count);

    /// <summary>
    /// Get the string buffer for direct access.
    /// </summary>
    public readonly ReadOnlySpan<char> GetStringBuffer() => _stringBuffer.Slice(0, _stringOffset);

    private static int ComputeHash(ReadOnlySpan<char> value)
    {
        // FNV-1a hash
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }
}

/// <summary>
/// Type of value in a binding.
/// </summary>
public enum BindingValueType : byte
{
    Unbound = 0,
    Uri = 1,
    String = 2,
    Integer = 3,
    Double = 4,
    Boolean = 5
}

/// <summary>
/// Single variable binding (unmanaged for stackalloc)
/// </summary>
public struct Binding
{
    public int VariableNameHash;
    public BindingValueType Type;
    public long IntegerValue;
    public double DoubleValue;
    public bool BooleanValue;
    public int StringOffset;
    public int StringLength;
}

public class SparqlParseException : Exception
{
    public SparqlParseException(string message) : base(message) { }
}
