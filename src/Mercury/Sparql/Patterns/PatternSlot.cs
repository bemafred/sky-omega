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
public enum PatternKind : byte
{
    Empty = 0,
    Triple = 1,
    Filter = 2,
    Bind = 3,
    GraphHeader = 4,
    ExistsHeader = 5,
    NotExistsHeader = 6,
}

/// <summary>
/// Fixed-size pattern element slot (64 bytes)
/// Provides typed view over raw bytes.
/// </summary>
public ref struct PatternSlot
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
    // Helpers
    // ───────────────────────────────────────────────────────────────────────
    
    public readonly bool IsTriple => Kind == PatternKind.Triple;
    public readonly bool IsFilter => Kind == PatternKind.Filter;
    public readonly bool IsBind => Kind == PatternKind.Bind;
    public readonly bool IsGraphHeader => Kind == PatternKind.GraphHeader;
    public readonly bool IsExistsHeader => Kind == PatternKind.ExistsHeader || Kind == PatternKind.NotExistsHeader;
    public readonly bool IsNegatedExists => Kind == PatternKind.NotExistsHeader;
    
    /// <summary>
    /// Clear slot for reuse
    /// </summary>
    public void Clear() => _span.Clear();
}

/// <summary>
/// Array of pattern slots - view over contiguous byte buffer
/// </summary>
public ref struct PatternArray
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
}

/// <summary>
/// Slice of a pattern array (for children)
/// </summary>
public ref struct PatternArraySlice
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
public ref struct PatternEnumerator
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
public ref struct TripleEnumerator
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
public ref struct FilterEnumerator
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

// ═══════════════════════════════════════════════════════════════════════════
// Extension methods for ergonomic creation
// ═══════════════════════════════════════════════════════════════════════════

public static class PatternArrayExtensions
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