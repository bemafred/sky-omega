using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Sparql.Patterns;

// ═══════════════════════════════════════════════════════════════════════════
// Discriminated Union via Span<byte> Views
// 
// Pattern: ref struct wraps Span<byte>, properties use MemoryMarshal.AsRef<T>
// Benefits: 
//   - Zero copy - direct memory access
//   - Storage-agnostic (stackalloc, array, mmap, pooled)
//   - True union semantics (overlapping interpretations)
//   - ref struct ensures stack discipline
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Pattern element discriminator
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This enum is internal because it is an
/// implementation detail of the query buffer pattern system.</para>
/// </remarks>
internal enum PatternKind : byte
{
    Empty = 0,
    Triple = 1,
    Filter = 2,
    Bind = 3,
    GraphHeader = 4,
    ExistsHeader = 5,
    NotExistsHeader = 6,
    MinusTriple = 7,     // MINUS pattern - same layout as Triple
    ValuesHeader = 8,    // VALUES clause header
    ValuesEntry = 9,     // VALUES clause entry
}

/// <summary>
/// Fixed-size pattern element slot (64 bytes)
/// Provides typed view over raw bytes.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This struct is internal because it is an
/// implementation detail of the zero-GC query buffer pattern system.</para>
/// </remarks>
internal ref struct PatternSlot
{
    public const int Size = 64;
    
    private readonly Span<byte> _span;
    
    public PatternSlot(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentOutOfRangeException(
                $"Buffer too small for PatternSlot. Required={Size}, Got={buffer.Length}");
        _span = buffer.Slice(0, Size);
    }
    
    // ───────────────────────────────────────────────────────────────────────
    // Byte 0: Kind discriminator
    // ───────────────────────────────────────────────────────────────────────
    
    public readonly ref PatternKind Kind => ref MemoryMarshal.AsRef<PatternKind>(_span.Slice(0, 1));
    
    // ───────────────────────────────────────────────────────────────────────
    // Variant: Triple (Kind == Triple)
    // Layout: [Kind:1][pad:3][Subject:12][Predicate:12][Object:12][PathType:1][pad:3][PathIri:12] = 56 bytes
    // ───────────────────────────────────────────────────────────────────────
    
    public ref TermType SubjectType => ref MemoryMarshal.AsRef<TermType>(_span.Slice(4, 1));
    public ref int SubjectStart => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    public ref int SubjectLength => ref MemoryMarshal.AsRef<int>(_span.Slice(12, 4));
    
    public ref TermType PredicateType => ref MemoryMarshal.AsRef<TermType>(_span.Slice(16, 1));
    public ref int PredicateStart => ref MemoryMarshal.AsRef<int>(_span.Slice(20, 4));
    public ref int PredicateLength => ref MemoryMarshal.AsRef<int>(_span.Slice(24, 4));
    
    public ref TermType ObjectType => ref MemoryMarshal.AsRef<TermType>(_span.Slice(28, 1));
    public ref int ObjectStart => ref MemoryMarshal.AsRef<int>(_span.Slice(32, 4));
    public ref int ObjectLength => ref MemoryMarshal.AsRef<int>(_span.Slice(36, 4));
    
    public ref PathType PathKind => ref MemoryMarshal.AsRef<PathType>(_span.Slice(40, 1));
    public ref int PathIriStart => ref MemoryMarshal.AsRef<int>(_span.Slice(44, 4));
    public ref int PathIriLength => ref MemoryMarshal.AsRef<int>(_span.Slice(48, 4));
    
    // ───────────────────────────────────────────────────────────────────────
    // Variant: Filter (Kind == Filter)
    // Layout: [Kind:1][pad:3][Start:4][Length:4] = 12 bytes
    // ───────────────────────────────────────────────────────────────────────
    
    public ref int FilterStart => ref MemoryMarshal.AsRef<int>(_span.Slice(4, 4));
    public ref int FilterLength => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    
    // ───────────────────────────────────────────────────────────────────────
    // Variant: Bind (Kind == Bind)
    // Layout: [Kind:1][pad:3][ExprStart:4][ExprLen:4][VarStart:4][VarLen:4] = 20 bytes
    // ───────────────────────────────────────────────────────────────────────
    
    public ref int BindExprStart => ref MemoryMarshal.AsRef<int>(_span.Slice(4, 4));
    public ref int BindExprLength => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    public ref int BindVarStart => ref MemoryMarshal.AsRef<int>(_span.Slice(12, 4));
    public ref int BindVarLength => ref MemoryMarshal.AsRef<int>(_span.Slice(16, 4));
    
    // ───────────────────────────────────────────────────────────────────────
    // Variant: GraphHeader (Kind == GraphHeader)
    // Layout: [Kind:1][pad:3][GraphTermType:1][pad:3][GraphStart:4][GraphLen:4][ChildStart:4][ChildCount:4] = 24 bytes
    // ───────────────────────────────────────────────────────────────────────
    
    public ref TermType GraphTermType => ref MemoryMarshal.AsRef<TermType>(_span.Slice(4, 1));
    public ref int GraphTermStart => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    public ref int GraphTermLength => ref MemoryMarshal.AsRef<int>(_span.Slice(12, 4));
    public ref int ChildStartIndex => ref MemoryMarshal.AsRef<int>(_span.Slice(16, 4));
    public ref int ChildCount => ref MemoryMarshal.AsRef<int>(_span.Slice(20, 4));
    
    // ───────────────────────────────────────────────────────────────────────
    // Variant: ExistsHeader / NotExistsHeader
    // Layout: [Kind:1][pad:3][ChildStart:4][ChildCount:4] = 12 bytes
    // (Same layout, Kind distinguishes EXISTS vs NOT EXISTS)
    // ───────────────────────────────────────────────────────────────────────
    
    public ref int ExistsChildStart => ref MemoryMarshal.AsRef<int>(_span.Slice(4, 4));
    public ref int ExistsChildCount => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));

    // ───────────────────────────────────────────────────────────────────────
    // Variant: ValuesHeader (Kind == ValuesHeader)
    // Layout: [Kind:1][pad:3][VarStart:4][VarLen:4][EntryCount:4] = 16 bytes
    // ───────────────────────────────────────────────────────────────────────

    public ref int ValuesVarStart => ref MemoryMarshal.AsRef<int>(_span.Slice(4, 4));
    public ref int ValuesVarLength => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    public ref int ValuesEntryCount => ref MemoryMarshal.AsRef<int>(_span.Slice(12, 4));

    // ───────────────────────────────────────────────────────────────────────
    // Variant: ValuesEntry (Kind == ValuesEntry)
    // Layout: [Kind:1][pad:3][Start:4][Length:4] = 12 bytes
    // ───────────────────────────────────────────────────────────────────────

    public ref int ValuesEntryStart => ref MemoryMarshal.AsRef<int>(_span.Slice(4, 4));
    public ref int ValuesEntryLength => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));

    // ───────────────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────────────

    public readonly bool IsTriple => Kind == PatternKind.Triple;
    public readonly bool IsMinusTriple => Kind == PatternKind.MinusTriple;
    public readonly bool IsFilter => Kind == PatternKind.Filter;
    public readonly bool IsBind => Kind == PatternKind.Bind;
    public readonly bool IsGraphHeader => Kind == PatternKind.GraphHeader;
    public readonly bool IsExistsHeader => Kind == PatternKind.ExistsHeader || Kind == PatternKind.NotExistsHeader;
    public readonly bool IsNegatedExists => Kind == PatternKind.NotExistsHeader;
    public readonly bool IsValuesHeader => Kind == PatternKind.ValuesHeader;
    public readonly bool IsValuesEntry => Kind == PatternKind.ValuesEntry;
    
    /// <summary>
    /// Clear slot for reuse
    /// </summary>
    public void Clear() => _span.Clear();
}

/// <summary>
/// Array of pattern slots - view over contiguous byte buffer
/// </summary>
internal ref struct PatternArray
{
    private readonly Span<byte> _buffer;
    private readonly int _capacity;
    private int _count;
    
    public PatternArray(Span<byte> buffer)
    {
        _buffer = buffer;
        _capacity = buffer.Length / PatternSlot.Size;
        _count = 0;
    }

    public PatternArray(Span<byte> buffer, int count)
    {
        _buffer = buffer;
        _capacity = buffer.Length / PatternSlot.Size;
        _count = count;
    }
    
    public readonly int Count => _count;
    public readonly int Capacity => _capacity;
    
    /// <summary>
    /// Get slot at index
    /// </summary>
    public PatternSlot this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count)
                throw new IndexOutOfRangeException();
            return new PatternSlot(_buffer.Slice(index * PatternSlot.Size, PatternSlot.Size));
        }
    }
    
    /// <summary>
    /// Allocate next slot
    /// </summary>
    public PatternSlot AllocateSlot()
    {
        if (_count >= _capacity)
            throw new InvalidOperationException("PatternArray is full");
        
        var slot = new PatternSlot(_buffer.Slice(_count * PatternSlot.Size, PatternSlot.Size));
        slot.Clear(); // Zero out for safety
        _count++;
        return slot;
    }
    
    /// <summary>
    /// Add a triple pattern
    /// </summary>
    public void AddTriple(
        TermType sType, int sStart, int sLen,
        TermType pType, int pStart, int pLen,
        TermType oType, int oStart, int oLen,
        PathType pathType = PathType.None, int pathStart = 0, int pathLen = 0)
    {
        var slot = AllocateSlot();
        slot.Kind = PatternKind.Triple;
        slot.SubjectType = sType;
        slot.SubjectStart = sStart;
        slot.SubjectLength = sLen;
        slot.PredicateType = pType;
        slot.PredicateStart = pStart;
        slot.PredicateLength = pLen;
        slot.ObjectType = oType;
        slot.ObjectStart = oStart;
        slot.ObjectLength = oLen;
        slot.PathKind = pathType;
        slot.PathIriStart = pathStart;
        slot.PathIriLength = pathLen;
    }
    
    /// <summary>
    /// Add a filter expression
    /// </summary>
    public void AddFilter(int start, int length)
    {
        var slot = AllocateSlot();
        slot.Kind = PatternKind.Filter;
        slot.FilterStart = start;
        slot.FilterLength = length;
    }
    
    /// <summary>
    /// Add a BIND expression
    /// </summary>
    public void AddBind(int exprStart, int exprLen, int varStart, int varLen)
    {
        var slot = AllocateSlot();
        slot.Kind = PatternKind.Bind;
        slot.BindExprStart = exprStart;
        slot.BindExprLength = exprLen;
        slot.BindVarStart = varStart;
        slot.BindVarLength = varLen;
    }
    
    /// <summary>
    /// Begin a GRAPH clause, returns header index
    /// </summary>
    public int BeginGraph(TermType termType, int termStart, int termLen)
    {
        int headerIndex = _count;
        var slot = AllocateSlot();
        slot.Kind = PatternKind.GraphHeader;
        slot.GraphTermType = termType;
        slot.GraphTermStart = termStart;
        slot.GraphTermLength = termLen;
        slot.ChildStartIndex = _count; // Children follow
        slot.ChildCount = 0;
        return headerIndex;
    }
    
    /// <summary>
    /// End a GRAPH clause
    /// </summary>
    public void EndGraph(int headerIndex)
    {
        var header = this[headerIndex];
        header.ChildCount = _count - header.ChildStartIndex;
    }
    
    /// <summary>
    /// Begin an EXISTS/NOT EXISTS filter
    /// </summary>
    public int BeginExists(bool negated)
    {
        int headerIndex = _count;
        var slot = AllocateSlot();
        slot.Kind = negated ? PatternKind.NotExistsHeader : PatternKind.ExistsHeader;
        slot.ExistsChildStart = _count;
        slot.ExistsChildCount = 0;
        return headerIndex;
    }
    
    /// <summary>
    /// End an EXISTS filter
    /// </summary>
    public void EndExists(int headerIndex)
    {
        var header = this[headerIndex];
        header.ExistsChildCount = _count - header.ExistsChildStart;
    }
    
    /// <summary>
    /// Get child patterns for a header slot
    /// </summary>
    public PatternArraySlice GetChildren(int headerIndex)
    {
        var header = this[headerIndex];
        return header.Kind switch
        {
            PatternKind.GraphHeader => new PatternArraySlice(_buffer, header.ChildStartIndex, header.ChildCount),
            PatternKind.ExistsHeader or PatternKind.NotExistsHeader => 
                new PatternArraySlice(_buffer, header.ExistsChildStart, header.ExistsChildCount),
            _ => default
        };
    }
    
    /// <summary>
    /// Get enumerator for all slots
    /// </summary>
    public PatternEnumerator GetEnumerator() => new(_buffer, _count);
    
    /// <summary>
    /// Get enumerator for triples only
    /// </summary>
    public TripleEnumerator EnumerateTriples() => new(_buffer, _count);
    
    /// <summary>
    /// Get enumerator for filters only
    /// </summary>
    public FilterEnumerator EnumerateFilters() => new(_buffer, _count);

    /// <summary>
    /// Get enumerator for graph headers only
    /// </summary>
    public GraphHeaderEnumerator EnumerateGraphHeaders() => new(_buffer, _count);

    /// <summary>
    /// Add a MINUS triple pattern (same layout as Triple but marked as MINUS)
    /// </summary>
    public void AddMinusTriple(
        TermType sType, int sStart, int sLen,
        TermType pType, int pStart, int pLen,
        TermType oType, int oStart, int oLen)
    {
        var slot = AllocateSlot();
        slot.Kind = PatternKind.MinusTriple;
        slot.SubjectType = sType;
        slot.SubjectStart = sStart;
        slot.SubjectLength = sLen;
        slot.PredicateType = pType;
        slot.PredicateStart = pStart;
        slot.PredicateLength = pLen;
        slot.ObjectType = oType;
        slot.ObjectStart = oStart;
        slot.ObjectLength = oLen;
    }

    /// <summary>
    /// Add a VALUES clause header, returns header index
    /// </summary>
    public int AddValuesHeader(int varStart, int varLen)
    {
        int headerIndex = _count;
        var slot = AllocateSlot();
        slot.Kind = PatternKind.ValuesHeader;
        slot.ValuesVarStart = varStart;
        slot.ValuesVarLength = varLen;
        slot.ValuesEntryCount = 0;
        return headerIndex;
    }

    /// <summary>
    /// Add a VALUES entry
    /// </summary>
    public void AddValuesEntry(int start, int length, int headerIndex)
    {
        var slot = AllocateSlot();
        slot.Kind = PatternKind.ValuesEntry;
        slot.ValuesEntryStart = start;
        slot.ValuesEntryLength = length;

        // Increment entry count in header
        var header = this[headerIndex];
        header.ValuesEntryCount++;
    }
}

/// <summary>
/// Slice of a pattern array (for children)
/// </summary>
internal ref struct PatternArraySlice
{
    private readonly Span<byte> _buffer;
    private readonly int _start;
    private readonly int _count;
    
    public PatternArraySlice(Span<byte> buffer, int start, int count)
    {
        _buffer = buffer;
        _start = start;
        _count = count;
    }
    
    public readonly int Count => _count;
    
    public PatternSlot this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new IndexOutOfRangeException();
            int offset = (_start + index) * PatternSlot.Size;
            return new PatternSlot(_buffer.Slice(offset, PatternSlot.Size));
        }
    }
    
    public PatternEnumerator GetEnumerator() => 
        new(_buffer.Slice(_start * PatternSlot.Size), _count);
}

/// <summary>
/// Enumerator over all pattern slots
/// </summary>
internal ref struct PatternEnumerator
{
    private readonly Span<byte> _buffer;
    private readonly int _count;
    private int _index;
    
    public PatternEnumerator(Span<byte> buffer, int count)
    {
        _buffer = buffer;
        _count = count;
        _index = -1;
    }
    
    public bool MoveNext() => ++_index < _count;
    
    public PatternSlot Current => new(_buffer.Slice(_index * PatternSlot.Size, PatternSlot.Size));
    
    public PatternEnumerator GetEnumerator() => this;
}

/// <summary>
/// Enumerator over triple patterns only
/// </summary>
internal ref struct TripleEnumerator
{
    private readonly Span<byte> _buffer;
    private readonly int _count;
    private int _index;
    
    public TripleEnumerator(Span<byte> buffer, int count)
    {
        _buffer = buffer;
        _count = count;
        _index = -1;
    }
    
    public bool MoveNext()
    {
        while (++_index < _count)
        {
            var kind = (PatternKind)_buffer[_index * PatternSlot.Size];
            if (kind == PatternKind.Triple)
                return true;
        }
        return false;
    }
    
    public PatternSlot Current => new(_buffer.Slice(_index * PatternSlot.Size, PatternSlot.Size));
    
    public TripleEnumerator GetEnumerator() => this;
}

/// <summary>
/// Enumerator over filter expressions only
/// </summary>
internal ref struct FilterEnumerator
{
    private readonly Span<byte> _buffer;
    private readonly int _count;
    private int _index;
    
    public FilterEnumerator(Span<byte> buffer, int count)
    {
        _buffer = buffer;
        _count = count;
        _index = -1;
    }
    
    public bool MoveNext()
    {
        while (++_index < _count)
        {
            var kind = (PatternKind)_buffer[_index * PatternSlot.Size];
            if (kind == PatternKind.Filter)
                return true;
        }
        return false;
    }
    
    public PatternSlot Current => new(_buffer.Slice(_index * PatternSlot.Size, PatternSlot.Size));

    public FilterEnumerator GetEnumerator() => this;
}

/// <summary>
/// Enumerator over graph header patterns only
/// </summary>
internal ref struct GraphHeaderEnumerator
{
    private readonly Span<byte> _buffer;
    private readonly int _count;
    private int _index;
    private int _headerIndex;  // 0-based index among graph headers

    public GraphHeaderEnumerator(Span<byte> buffer, int count)
    {
        _buffer = buffer;
        _count = count;
        _index = -1;
        _headerIndex = -1;
    }

    public bool MoveNext()
    {
        while (++_index < _count)
        {
            var kind = (PatternKind)_buffer[_index * PatternSlot.Size];
            if (kind == PatternKind.GraphHeader)
            {
                _headerIndex++;
                return true;
            }
        }
        return false;
    }

    public PatternSlot Current => new(_buffer.Slice(_index * PatternSlot.Size, PatternSlot.Size));

    /// <summary>
    /// Current index in the pattern array (for GetChildren)
    /// </summary>
    public int CurrentIndex => _index;

    /// <summary>
    /// 0-based index among graph headers (0 for first, 1 for second, etc.)
    /// </summary>
    public int HeaderIndex => _headerIndex;

    public GraphHeaderEnumerator GetEnumerator() => this;
}

// ═══════════════════════════════════════════════════════════════════════════
// Extension methods for ergonomic creation
// ═══════════════════════════════════════════════════════════════════════════

internal static class PatternArrayExtensions
{
    /// <summary>
    /// Create pattern array from byte array
    /// </summary>
    public static PatternArray AsPatternArray(this byte[] buffer)
        => new(buffer.AsSpan());
    
    /// <summary>
    /// Create pattern array from span
    /// </summary>
    public static PatternArray AsPatternArray(this Span<byte> buffer)
        => new(buffer);
    
    /// <summary>
    /// Required buffer size for N patterns
    /// </summary>
    public static int PatternBufferSize(int patternCount)
        => patternCount * PatternSlot.Size;
}

// ═══════════════════════════════════════════════════════════════════════════
// Usage Examples
// ═══════════════════════════════════════════════════════════════════════════

/*
// Stack allocation - 32 patterns = 2048 bytes
Span<byte> buffer = stackalloc byte[PatternArrayExtensions.PatternBufferSize(32)];
var patterns = buffer.AsPatternArray();

// Add patterns
patterns.AddTriple(
    TermType.Variable, 10, 2,   // ?s at offset 10, len 2
    TermType.Iri, 20, 15,       // <http://...> at offset 20, len 15  
    TermType.Variable, 40, 2    // ?o at offset 40, len 2
);

patterns.AddFilter(100, 25);    // FILTER expression at offset 100, len 25

// GRAPH clause with children
int graphHeader = patterns.BeginGraph(TermType.Iri, 200, 30);
patterns.AddTriple(...);  // child pattern
patterns.AddTriple(...);  // child pattern
patterns.EndGraph(graphHeader);

// Iterate all
foreach (var slot in patterns)
{
    switch (slot.Kind)
    {
        case PatternKind.Triple:
            Console.WriteLine($"Triple: {slot.SubjectStart}...");
            break;
        case PatternKind.Filter:
            Console.WriteLine($"Filter: {slot.FilterStart}, {slot.FilterLength}");
            break;
    }
}

// Iterate triples only
foreach (var slot in patterns.EnumerateTriples())
{
    // slot.Kind is guaranteed to be Triple here
    var sStart = slot.SubjectStart;
    var sLen = slot.SubjectLength;
}

// Access children
var children = patterns.GetChildren(graphHeader);
foreach (var child in children)
{
    // Process child patterns...
}

// From pooled/array storage
byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent(2048);
try
{
    var patterns = pooledBuffer.AsPatternArray();
    // ... use patterns ...
}
finally
{
    ArrayPool<byte>.Shared.Return(pooledBuffer);
}
*/

// ═══════════════════════════════════════════════════════════════════════════
// QueryBuffer - Pooled storage for parsed SPARQL queries
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Owns pooled buffer storage for a parsed SPARQL query.
/// Replaces the large inline struct approach with Buffer + View pattern.
/// </summary>
/// <remarks>
/// Buffer layout:
/// - Main patterns: offset 0, DefaultCapacity slots
/// - Subquery patterns: allocated on demand
///
/// This class is the antidote to large inline struct buffers:
/// - ~100 bytes vs ~9KB for old Query struct
/// - Pooled memory = zero-GC
/// - No stack overflow on nested calls
/// </remarks>
internal sealed class QueryBuffer : IDisposable
{
    /// <summary>Default capacity in pattern slots (64 bytes each)</summary>
    public const int DefaultCapacity = 128;  // 8KB buffer - handles most queries

    /// <summary>Maximum capacity to prevent runaway allocation</summary>
    public const int MaxCapacity = 1024;     // 64KB max

    private byte[]? _buffer;
    private int _patternCount;
    private bool _disposed;

    // Query metadata (lightweight - no large inline storage)
    public QueryType Type { get; set; }
    public bool SelectDistinct { get; set; }
    public bool SelectAll { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }

    // Source span offsets for lazily-evaluated expressions
    public int BaseUriStart { get; set; }
    public int BaseUriLength { get; set; }

    // Dataset clauses (heap array is fine - small, infrequent)
    public DatasetClause[]? Datasets { get; set; }

    // Prefix mappings (heap array - typically small)
    public PrefixMapping[]? Prefixes { get; set; }

    // Order by offsets
    public OrderByEntry[]? OrderBy { get; set; }

    // Group by variable offsets
    public GroupByEntry[]? GroupBy { get; set; }

    // Aggregate expressions
    public AggregateEntry[]? Aggregates { get; set; }

    // Pattern metadata (computed during parsing, cached for fast access)
    public int FilterCount { get; set; }
    public int BindCount { get; set; }
    public int FirstBranchBindCount { get; set; }  // BINDs in first UNION branch
    public int MinusPatternCount { get; set; }
    public int ExistsFilterCount { get; set; }
    public int UnionStartIndex { get; set; }  // Index where UNION branch starts
    public bool HasUnionFlag { get; set; }    // True if UNION was encountered
    public uint OptionalFlags { get; set; }   // Bitmask for optional patterns
    public bool HasValues { get; set; }

    // Graph clause metadata (cached to avoid large struct copies)
    public int GraphClauseCount { get; set; }
    public bool FirstGraphIsVariable { get; set; }
    public int FirstGraphPatternCount { get; set; }
    public int TriplePatternCount { get; set; }  // Count of direct triple patterns (not in GRAPH)
    public bool HasSubQueries { get; set; }
    public bool HasService { get; set; }

    // HAVING clause (expression offsets into source)
    public int HavingStart { get; set; }
    public int HavingLength { get; set; }

    // DESCRIBE query metadata
    public bool DescribeAll { get; set; }

    // Temporal query parameters (ADR-009 Phase 2)
    public TemporalQueryMode TemporalMode { get; set; }
    public int TimeStartStart { get; set; }
    public int TimeStartLength { get; set; }
    public int TimeEndStart { get; set; }
    public int TimeEndLength { get; set; }

    // CONSTRUCT template patterns (heap-allocated for simplicity)
    public TriplePattern[]? ConstructPatterns { get; set; }

    // SelectClause data needed for QueryResults (stored on heap to avoid 8KB Query struct)
    public SelectClauseData? SelectData { get; set; }

    // Computed properties
    public bool HasFilters => FilterCount > 0;
    public bool HasBinds => BindCount > 0;
    public bool HasMinus => MinusPatternCount > 0;
    public bool HasExists => ExistsFilterCount > 0;
    public bool HasUnion => HasUnionFlag;
    public bool HasOptionalPatterns => OptionalFlags != 0;
    public int UnionBranchPatternCount => HasUnion ? PatternCount - UnionStartIndex : 0;
    public int UnionBranchBindCount => HasUnion ? BindCount - FirstBranchBindCount : 0;

    /// <summary>
    /// Count of triple patterns in the union branch (excludes BINDs, FILTERs, etc.)
    /// </summary>
    public int UnionBranchTripleCount
    {
        get
        {
            if (!HasUnion) return 0;
            int count = 0;
            var patterns = GetPatterns();
            for (int i = UnionStartIndex; i < PatternCount; i++)
            {
                if (patterns[i].Kind == PatternKind.Triple)
                    count++;
            }
            return count;
        }
    }
    public bool HasGraph => GraphClauseCount > 0;
    public bool HasHaving => HavingLength > 0;
    public bool HasConstruct => ConstructPatterns != null && ConstructPatterns.Length > 0;

    /// <summary>
    /// Create a new query buffer with default capacity
    /// </summary>
    public QueryBuffer() : this(DefaultCapacity) { }

    /// <summary>
    /// Create a new query buffer with specified capacity
    /// </summary>
    public QueryBuffer(int capacitySlots)
    {
        if (capacitySlots > MaxCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacitySlots),
                $"Capacity {capacitySlots} exceeds maximum {MaxCapacity}");

        int bufferSize = capacitySlots * PatternSlot.Size;
        _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
        _buffer.AsSpan(0, bufferSize).Clear(); // Zero-init for safety
    }

    /// <summary>
    /// Get pattern array view for writing/reading.
    /// When PatternCount > 0 (after parsing), returns read-only view with count.
    /// When PatternCount == 0 (during parsing), returns writable view.
    /// </summary>
    public PatternArray GetPatterns()
    {
        ThrowIfDisposed();
        return _patternCount > 0
            ? new PatternArray(_buffer.AsSpan(), _patternCount)
            : new PatternArray(_buffer.AsSpan());
    }

    /// <summary>
    /// Pattern count after parsing
    /// </summary>
    public int PatternCount
    {
        get => _patternCount;
        set => _patternCount = value;
    }

    /// <summary>
    /// Capacity in pattern slots
    /// </summary>
    public int Capacity => _buffer?.Length / PatternSlot.Size ?? 0;

    /// <summary>
    /// Get HavingClause from stored offsets.
    /// </summary>
    public HavingClause GetHavingClause()
    {
        return new HavingClause
        {
            ExpressionStart = HavingStart,
            ExpressionLength = HavingLength
        };
    }

    /// <summary>
    /// Get SelectClause from stored data.
    /// Returns a lightweight copy since QueryResults needs it for aggregate processing.
    /// </summary>
    public SelectClause GetSelectClause()
    {
        return SelectData?.ToSelectClause() ?? default;
    }

    /// <summary>
    /// Get OrderByClause from stored entries.
    /// </summary>
    public OrderByClause GetOrderByClause()
    {
        var clause = new OrderByClause();
        if (OrderBy != null)
        {
            foreach (var entry in OrderBy)
            {
                clause.AddCondition(entry.VariableStart, entry.VariableLength,
                    entry.Descending ? OrderDirection.Descending : OrderDirection.Ascending);
            }
        }
        return clause;
    }

    /// <summary>
    /// Get GroupByClause from stored entries.
    /// </summary>
    public GroupByClause GetGroupByClause()
    {
        var clause = new GroupByClause();
        if (GroupBy != null)
        {
            foreach (var entry in GroupBy)
            {
                // AddExpression handles both simple variables (exprLength=0) and expressions
                clause.AddExpression(entry.VariableStart, entry.VariableLength,
                    entry.ExpressionStart, entry.ExpressionLength);
            }
        }
        return clause;
    }

    /// <summary>
    /// Extract triple patterns to a heap-allocated array.
    /// Used by MultiPatternScan to avoid copying the entire GraphPattern struct.
    /// </summary>
    public TriplePattern[] GetTriplePatternsArray()
    {
        ThrowIfDisposed();
        var patterns = GetPatterns();
        var tripleList = new System.Collections.Generic.List<TriplePattern>();

        foreach (var slot in patterns.EnumerateTriples())
        {
            var tp = new TriplePattern
            {
                Subject = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength },
                Predicate = new Term { Type = slot.PredicateType, Start = slot.PredicateStart, Length = slot.PredicateLength },
                Object = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength },
                Path = new PropertyPath { Type = slot.PathKind, Iri = new Term { Start = slot.PathIriStart, Length = slot.PathIriLength } }
            };
            tripleList.Add(tp);
        }

        return tripleList.ToArray();
    }

    /// <summary>
    /// Get ConstructTemplate from stored patterns.
    /// </summary>
    public ConstructTemplate GetConstructTemplate()
    {
        var template = new ConstructTemplate();
        if (ConstructPatterns != null)
        {
            foreach (var tp in ConstructPatterns)
            {
                template.AddPattern(tp);
            }
        }
        return template;
    }

    /// <summary>
    /// Build a GraphPattern from stored data.
    /// This creates a copy on the heap for operators that still need GraphPattern.
    /// </summary>
    public GraphPattern BuildGraphPattern()
    {
        ThrowIfDisposed();
        var gp = new GraphPattern();
        var patterns = GetPatterns();
        int tripleIndex = 0;

        for (int i = 0; i < patterns.Count; i++)
        {
            var slot = patterns[i];
            switch (slot.Kind)
            {
                case PatternKind.Triple:
                    var tp = new TriplePattern
                    {
                        Subject = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength },
                        Predicate = new Term { Type = slot.PredicateType, Start = slot.PredicateStart, Length = slot.PredicateLength },
                        Object = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength },
                        Path = new PropertyPath { Type = slot.PathKind, Iri = new Term { Start = slot.PathIriStart, Length = slot.PathIriLength } }
                    };
                    // Check if this pattern should be optional
                    if ((OptionalFlags & (1u << tripleIndex)) != 0)
                        gp.AddOptionalPattern(tp);
                    else
                        gp.AddPattern(tp);
                    tripleIndex++;
                    break;
                case PatternKind.Filter:
                    gp.AddFilter(new FilterExpr { Start = slot.FilterStart, Length = slot.FilterLength });
                    break;
                case PatternKind.Bind:
                    gp.AddBind(new BindExpr
                    {
                        ExprStart = slot.BindExprStart,
                        ExprLength = slot.BindExprLength,
                        VarStart = slot.BindVarStart,
                        VarLength = slot.BindVarLength
                    });
                    break;
            }
        }

        return gp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_buffer != null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QueryBuffer));
    }
}

/// <summary>
/// Prefix mapping (prefix -> IRI)
/// </summary>
internal struct PrefixMapping
{
    public int PrefixStart;
    public int PrefixLength;
    public int IriStart;
    public int IriLength;
}

/// <summary>
/// ORDER BY entry
/// </summary>
internal struct OrderByEntry
{
    public int VariableStart;
    public int VariableLength;
    public bool Descending;
}

/// <summary>
/// GROUP BY entry
/// </summary>
internal struct GroupByEntry
{
    public int VariableStart;
    public int VariableLength;
    public int ExpressionStart;
    public int ExpressionLength;
}

/// <summary>
/// Aggregate expression entry
/// </summary>
internal struct AggregateEntry
{
    public AggregateFunction Function;
    public int VariableStart;
    public int VariableLength;
    public int AliasStart;
    public int AliasLength;
    public bool Distinct;
    public int SeparatorStart;  // For GROUP_CONCAT
    public int SeparatorLength;
}

/// <summary>
/// Heap-allocated SelectClause data to avoid storing the full 8KB Query struct.
/// Used by QueryResults for aggregate processing.
/// </summary>
internal sealed class SelectClauseData
{
    public bool Distinct { get; init; }
    public bool Reduced { get; init; }
    public bool SelectAll { get; init; }
    public bool HasAggregates { get; init; }
    public AggregateEntry[]? Aggregates { get; init; }

    /// <summary>
    /// Create SelectClauseData from a SelectClause struct.
    /// </summary>
    public static SelectClauseData FromSelectClause(in SelectClause clause)
    {
        AggregateEntry[]? aggs = null;
        if (clause.HasAggregates)
        {
            aggs = new AggregateEntry[clause.AggregateCount];
            for (int i = 0; i < clause.AggregateCount; i++)
            {
                var src = clause.GetAggregate(i);
                aggs[i] = new AggregateEntry
                {
                    Function = src.Function,
                    VariableStart = src.VariableStart,
                    VariableLength = src.VariableLength,
                    AliasStart = src.AliasStart,
                    AliasLength = src.AliasLength,
                    Distinct = src.Distinct,
                    SeparatorStart = src.SeparatorStart,
                    SeparatorLength = src.SeparatorLength
                };
            }
        }

        return new SelectClauseData
        {
            Distinct = clause.Distinct,
            Reduced = clause.Reduced,
            SelectAll = clause.SelectAll,
            HasAggregates = clause.HasAggregates,
            Aggregates = aggs
        };
    }

    /// <summary>
    /// Convert back to a SelectClause struct for compatibility.
    /// </summary>
    public SelectClause ToSelectClause()
    {
        var clause = new SelectClause
        {
            Distinct = Distinct,
            Reduced = Reduced,
            SelectAll = SelectAll
        };

        if (Aggregates != null)
        {
            foreach (var agg in Aggregates)
            {
                clause.AddAggregate(new AggregateExpression
                {
                    Function = agg.Function,
                    VariableStart = agg.VariableStart,
                    VariableLength = agg.VariableLength,
                    AliasStart = agg.AliasStart,
                    AliasLength = agg.AliasLength,
                    Distinct = agg.Distinct,
                    SeparatorStart = agg.SeparatorStart,
                    SeparatorLength = agg.SeparatorLength
                });
            }
        }

        return clause;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// QueryBufferAdapter - Bridge from old Query struct to new QueryBuffer
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Converts the old large Query struct into the new QueryBuffer format.
/// This enables incremental migration: executor can use QueryBuffer while
/// parser still produces Query struct.
/// </summary>
internal static class QueryBufferAdapter
{
    /// <summary>
    /// Create a QueryBuffer from an existing Query struct.
    /// Caller owns the returned buffer and must dispose it.
    /// </summary>
    public static QueryBuffer FromQuery(in Query query, ReadOnlySpan<char> source)
    {
        // Estimate capacity: patterns + filters + binds + graph patterns
        int estimatedSlots = query.WhereClause.Pattern.PatternCount
            + query.WhereClause.Pattern.FilterCount
            + query.WhereClause.Pattern.BindCount
            + query.WhereClause.Pattern.GraphClauseCount * 10
            + query.WhereClause.Pattern.ExistsFilterCount * 5
            + 16; // Buffer room

        var buffer = new QueryBuffer(Math.Max(estimatedSlots, 32));

        try
        {
            PopulateBuffer(buffer, in query, source);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static void PopulateBuffer(QueryBuffer buffer, in Query query, ReadOnlySpan<char> source)
    {
        // Copy metadata
        buffer.Type = query.Type;
        buffer.SelectDistinct = query.SelectClause.Distinct || query.SelectClause.Reduced;
        buffer.SelectAll = query.SelectClause.SelectAll;
        buffer.Limit = query.SolutionModifier.Limit;
        buffer.Offset = query.SolutionModifier.Offset;
        buffer.BaseUriStart = query.Prologue.BaseUriStart;
        buffer.BaseUriLength = query.Prologue.BaseUriLength;

        // Copy datasets
        if (query.Datasets != null && query.Datasets.Length > 0)
        {
            buffer.Datasets = query.Datasets;
        }

        // Copy aggregates
        if (query.SelectClause.HasAggregates)
        {
            var aggs = new AggregateEntry[query.SelectClause.AggregateCount];
            for (int i = 0; i < query.SelectClause.AggregateCount; i++)
            {
                var src = query.SelectClause.GetAggregate(i);
                aggs[i] = new AggregateEntry
                {
                    Function = src.Function,
                    VariableStart = src.VariableStart,
                    VariableLength = src.VariableLength,
                    AliasStart = src.AliasStart,
                    AliasLength = src.AliasLength,
                    Distinct = src.Distinct,
                    SeparatorStart = src.SeparatorStart,
                    SeparatorLength = src.SeparatorLength
                };
            }
            buffer.Aggregates = aggs;
        }

        // Copy ORDER BY
        if (query.SolutionModifier.OrderBy.HasOrderBy)
        {
            var orderBy = query.SolutionModifier.OrderBy;
            var entries = new OrderByEntry[orderBy.Count];
            for (int i = 0; i < orderBy.Count; i++)
            {
                var cond = orderBy.GetCondition(i);
                entries[i] = new OrderByEntry
                {
                    VariableStart = cond.VariableStart,
                    VariableLength = cond.VariableLength,
                    Descending = cond.Direction == OrderDirection.Descending
                };
            }
            buffer.OrderBy = entries;
        }

        // Copy GROUP BY
        if (query.SolutionModifier.GroupBy.HasGroupBy)
        {
            var groupBy = query.SolutionModifier.GroupBy;
            var entries = new GroupByEntry[groupBy.Count];
            for (int i = 0; i < groupBy.Count; i++)
            {
                var (varStart, varLength) = groupBy.GetVariable(i);
                var (exprStart, exprLength) = groupBy.GetExpression(i);
                entries[i] = new GroupByEntry
                {
                    VariableStart = varStart,
                    VariableLength = varLength,
                    ExpressionStart = exprStart,
                    ExpressionLength = exprLength
                };
            }
            buffer.GroupBy = entries;
        }

        // Copy pattern metadata from GraphPattern
        ref readonly var gp = ref query.WhereClause.Pattern;
        buffer.FilterCount = gp.FilterCount;
        buffer.BindCount = gp.BindCount;
        buffer.FirstBranchBindCount = gp.FirstBranchBindCount;
        buffer.MinusPatternCount = gp.MinusPatternCount;
        buffer.ExistsFilterCount = gp.ExistsFilterCount;
        // UnionStartIndex in QueryBuffer accounts for linearized slot order:
        // [all triples] + [all filters] + [all binds]
        // The union branch starts after first-branch triples + all filters + first-branch binds
        buffer.UnionStartIndex = gp.HasUnion
            ? gp.FirstBranchPatternCount + gp.FilterCount + gp.FirstBranchBindCount
            : 0;
        buffer.HasUnionFlag = gp.HasUnion;
        buffer.HasValues = gp.HasValues;
        // Copy OptionalFlags - build bitmask from GraphPattern's IsOptional checks
        uint optFlags = 0;
        for (int i = 0; i < gp.PatternCount && i < 32; i++)
        {
            if (gp.IsOptional(i))
                optFlags |= (1u << i);
        }
        buffer.OptionalFlags = optFlags;

        // Copy graph clause metadata (cached to avoid large struct copies in QueryExecutor)
        buffer.GraphClauseCount = gp.GraphClauseCount;
        buffer.TriplePatternCount = gp.PatternCount;
        buffer.HasSubQueries = gp.HasSubQueries;
        buffer.HasService = gp.HasService;
        if (gp.GraphClauseCount > 0)
        {
            buffer.FirstGraphIsVariable = gp.IsGraphClauseVariable(0);
            buffer.FirstGraphPatternCount = gp.GetGraphClausePatternCount(0);
        }

        // Copy HAVING clause (ADR-009 Phase 2)
        buffer.HavingStart = query.SolutionModifier.Having.ExpressionStart;
        buffer.HavingLength = query.SolutionModifier.Having.ExpressionLength;

        // Copy DESCRIBE metadata
        buffer.DescribeAll = query.DescribeAll;

        // Copy CONSTRUCT template patterns
        if (query.ConstructTemplate.PatternCount > 0)
        {
            buffer.ConstructPatterns = new TriplePattern[query.ConstructTemplate.PatternCount];
            for (int i = 0; i < query.ConstructTemplate.PatternCount; i++)
            {
                buffer.ConstructPatterns[i] = query.ConstructTemplate.GetPattern(i);
            }
        }

        // Copy SelectClause data to heap (avoids storing 8KB Query struct)
        buffer.SelectData = SelectClauseData.FromSelectClause(in query.SelectClause);

        // Copy temporal clause parameters (ADR-009 Phase 2)
        buffer.TemporalMode = query.SolutionModifier.Temporal.Mode;
        buffer.TimeStartStart = query.SolutionModifier.Temporal.TimeStartStart;
        buffer.TimeStartLength = query.SolutionModifier.Temporal.TimeStartLength;
        buffer.TimeEndStart = query.SolutionModifier.Temporal.TimeEndStart;
        buffer.TimeEndLength = query.SolutionModifier.Temporal.TimeEndLength;

        // Copy prefix mappings from Prologue
        if (query.Prologue.PrefixCount > 0)
        {
            var prefixes = new PrefixMapping[query.Prologue.PrefixCount];
            for (int i = 0; i < query.Prologue.PrefixCount; i++)
            {
                var (prefixStart, prefixLength, iriStart, iriLength) = query.Prologue.GetPrefix(i);
                prefixes[i] = new PrefixMapping
                {
                    PrefixStart = prefixStart,
                    PrefixLength = prefixLength,
                    IriStart = iriStart,
                    IriLength = iriLength
                };
            }
            buffer.Prefixes = prefixes;
        }

        // Convert patterns to slots
        var patterns = buffer.GetPatterns();
        ConvertGraphPattern(ref patterns, in query.WhereClause.Pattern, source);
        buffer.PatternCount = patterns.Count;
    }

    private static void ConvertGraphPattern(ref PatternArray patterns, in GraphPattern gp, ReadOnlySpan<char> source)
    {
        // Add triple patterns
        for (int i = 0; i < gp.PatternCount; i++)
        {
            var tp = gp.GetPattern(i);
            patterns.AddTriple(
                tp.Subject.Type, tp.Subject.Start, tp.Subject.Length,
                tp.Predicate.Type, tp.Predicate.Start, tp.Predicate.Length,
                tp.Object.Type, tp.Object.Start, tp.Object.Length,
                tp.Path.Type, tp.Path.Iri.Start, tp.Path.Iri.Length
            );
        }

        // Add filters
        for (int i = 0; i < gp.FilterCount; i++)
        {
            var f = gp.GetFilter(i);
            patterns.AddFilter(f.Start, f.Length);
        }

        // Add binds
        for (int i = 0; i < gp.BindCount; i++)
        {
            var b = gp.GetBind(i);
            patterns.AddBind(b.ExprStart, b.ExprLength, b.VarStart, b.VarLength);
        }

        // Add GRAPH clauses
        for (int i = 0; i < gp.GraphClauseCount; i++)
        {
            var gc = gp.GetGraphClause(i);
            int headerIndex = patterns.BeginGraph(gc.Graph.Type, gc.Graph.Start, gc.Graph.Length);

            // Add patterns within GRAPH clause
            for (int j = 0; j < gc.PatternCount; j++)
            {
                var tp = gc.GetPattern(j);
                patterns.AddTriple(
                    tp.Subject.Type, tp.Subject.Start, tp.Subject.Length,
                    tp.Predicate.Type, tp.Predicate.Start, tp.Predicate.Length,
                    tp.Object.Type, tp.Object.Start, tp.Object.Length,
                    tp.Path.Type, tp.Path.Iri.Start, tp.Path.Iri.Length
                );
            }

            patterns.EndGraph(headerIndex);
        }

        // Add EXISTS filters
        for (int i = 0; i < gp.ExistsFilterCount; i++)
        {
            var ef = gp.GetExistsFilter(i);
            int headerIndex = patterns.BeginExists(ef.Negated);

            for (int j = 0; j < ef.PatternCount; j++)
            {
                var tp = ef.GetPattern(j);
                patterns.AddTriple(
                    tp.Subject.Type, tp.Subject.Start, tp.Subject.Length,
                    tp.Predicate.Type, tp.Predicate.Start, tp.Predicate.Length,
                    tp.Object.Type, tp.Object.Start, tp.Object.Length
                );
            }

            patterns.EndExists(headerIndex);
        }

        // Add MINUS patterns
        for (int i = 0; i < gp.MinusPatternCount; i++)
        {
            var mp = gp.GetMinusPattern(i);
            patterns.AddMinusTriple(
                mp.Subject.Type, mp.Subject.Start, mp.Subject.Length,
                mp.Predicate.Type, mp.Predicate.Start, mp.Predicate.Length,
                mp.Object.Type, mp.Object.Start, mp.Object.Length
            );
        }

        // Add VALUES clause if present
        if (gp.HasValues)
        {
            var values = gp.Values;
            int headerIdx = patterns.AddValuesHeader(values.VarStart, values.VarLength);
            for (int i = 0; i < values.ValueCount; i++)
            {
                var (start, len) = values.GetValue(i);
                patterns.AddValuesEntry(start, len, headerIdx);
            }
        }
    }
}