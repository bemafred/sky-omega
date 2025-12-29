// Discriminated Union Prototype for Zero-GC SPARQL Pattern Storage
// Addresses stack overflow from large inline struct arrays
//
// Key insight: Most queries use few pattern types. Embedding all 7 types
// wastes ~8KB per GraphPattern. A discriminated union uses ONE array
// with type tags, reducing typical usage to ~500 bytes.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Sparql.Experimental;

/// <summary>
/// Pattern element type discriminator (1 byte)
/// </summary>
public enum PatternElementKind : byte
{
Empty = 0,
Triple,           // TriplePattern
Filter,           // FilterExpr
Bind,             // BindExpr
MinusTriple,      // TriplePattern in MINUS context
Exists,           // ExistsFilter header
NotExists,        // ExistsFilter header (negated)
GraphClause,      // GraphClause header
SubSelect,        // SubSelect header (requires external storage)
Values,           // ValuesClause header
}

/// <summary>
/// Unified pattern element - fixed size discriminated union.
///
/// Design choices:
/// - 64 bytes per slot (cache-line friendly, fits largest common element)
/// - Type tag + payload in single struct
/// - Complex nested types (SubSelect) use indirection
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PatternElement
{
[FieldOffset(0)] public PatternElementKind Kind;

```
// Overlay: TriplePattern (most common) - 60 bytes max
[FieldOffset(4)] public Term Subject;
[FieldOffset(16)] public Term Predicate;
[FieldOffset(28)] public Term Object;
[FieldOffset(40)] public PropertyPath Path;

// Overlay: FilterExpr - 8 bytes
[FieldOffset(4)] public int FilterStart;
[FieldOffset(8)] public int FilterLength;

// Overlay: BindExpr - 16 bytes
[FieldOffset(4)] public int BindExprStart;
[FieldOffset(8)] public int BindExprLength;
[FieldOffset(12)] public int BindVarStart;
[FieldOffset(16)] public int BindVarLength;

// Overlay: GraphClause header - 12 bytes + child index
[FieldOffset(4)] public Term GraphTerm;
[FieldOffset(16)] public int GraphChildStart;   // Index of first child pattern
[FieldOffset(20)] public int GraphChildCount;   // Number of child patterns

// Overlay: Exists header - child range
[FieldOffset(4)] public int ExistsChildStart;
[FieldOffset(8)] public int ExistsChildCount;

// Overlay: SubSelect - requires pooled storage (too large for inline)
[FieldOffset(4)] public int SubSelectPoolIndex;

// Overlay: Values clause header
[FieldOffset(4)] public int ValuesVarStart;
[FieldOffset(8)] public int ValuesVarLength;
[FieldOffset(12)] public int ValuesDataStart;   // Index to values data
[FieldOffset(16)] public int ValuesDataCount;
```

}

/// <summary>
/// Compact graph pattern using discriminated union array.
///
/// Stack size: 32 elements × 64 bytes = 2,048 bytes (vs 9,000 bytes original)
/// Typical query (3-5 patterns): effective usage ~320 bytes
/// </summary>
public struct CompactGraphPattern
{
public const int MaxElements = 32;

```
private int _count;

// Single unified array - all pattern types share this space
private PatternElement _e0, _e1, _e2, _e3, _e4, _e5, _e6, _e7;
private PatternElement _e8, _e9, _e10, _e11, _e12, _e13, _e14, _e15;
private PatternElement _e16, _e17, _e18, _e19, _e20, _e21, _e22, _e23;
private PatternElement _e24, _e25, _e26, _e27, _e28, _e29, _e30, _e31;

public readonly int Count => _count;

// ═══════════════════════════════════════════════════════════════════════
// Span Extension Pattern: Type-safe views over the unified buffer
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Get all elements as a span for iteration
/// </summary>
public readonly Span<PatternElement> AsSpan()
{
    // This is the key: single contiguous buffer accessible via Span
    ref var first = ref Unsafe.AsRef(in _e0);
    return MemoryMarshal.CreateSpan(ref first, _count);
}

/// <summary>
/// Add a triple pattern
/// </summary>
public void AddTriple(Term subject, Term predicate, Term obj, PropertyPath path = default)
{
    if (_count >= MaxElements) return;
    
    ref var element = ref GetElementRef(_count++);
    element.Kind = PatternElementKind.Triple;
    element.Subject = subject;
    element.Predicate = predicate;
    element.Object = obj;
    element.Path = path;
}

/// <summary>
/// Add a filter expression
/// </summary>
public void AddFilter(int start, int length)
{
    if (_count >= MaxElements) return;
    
    ref var element = ref GetElementRef(_count++);
    element.Kind = PatternElementKind.Filter;
    element.FilterStart = start;
    element.FilterLength = length;
}

/// <summary>
/// Add a BIND expression
/// </summary>
public void AddBind(int exprStart, int exprLength, int varStart, int varLength)
{
    if (_count >= MaxElements) return;
    
    ref var element = ref GetElementRef(_count++);
    element.Kind = PatternElementKind.Bind;
    element.BindExprStart = exprStart;
    element.BindExprLength = exprLength;
    element.BindVarStart = varStart;
    element.BindVarLength = varLength;
}

/// <summary>
/// Begin a GRAPH clause - returns index for adding child patterns
/// </summary>
public int BeginGraphClause(Term graphTerm)
{
    if (_count >= MaxElements) return -1;
    
    int headerIndex = _count++;
    ref var element = ref GetElementRef(headerIndex);
    element.Kind = PatternElementKind.GraphClause;
    element.GraphTerm = graphTerm;
    element.GraphChildStart = _count;  // Children follow immediately
    element.GraphChildCount = 0;
    
    return headerIndex;
}

/// <summary>
/// End a GRAPH clause by recording child count
/// </summary>
public void EndGraphClause(int headerIndex)
{
    ref var element = ref GetElementRef(headerIndex);
    element.GraphChildCount = _count - element.GraphChildStart;
}

/// <summary>
/// Begin an EXISTS/NOT EXISTS filter
/// </summary>
public int BeginExists(bool negated)
{
    if (_count >= MaxElements) return -1;
    
    int headerIndex = _count++;
    ref var element = ref GetElementRef(headerIndex);
    element.Kind = negated ? PatternElementKind.NotExists : PatternElementKind.Exists;
    element.ExistsChildStart = _count;
    element.ExistsChildCount = 0;
    
    return headerIndex;
}

/// <summary>
/// End an EXISTS filter
/// </summary>
public void EndExists(int headerIndex)
{
    ref var element = ref GetElementRef(headerIndex);
    element.ExistsChildCount = _count - element.ExistsChildStart;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private ref PatternElement GetElementRef(int index)
{
    // Manual switch - compiler optimizes this well
    return ref index switch
    {
        0 => ref _e0, 1 => ref _e1, 2 => ref _e2, 3 => ref _e3,
        4 => ref _e4, 5 => ref _e5, 6 => ref _e6, 7 => ref _e7,
        8 => ref _e8, 9 => ref _e9, 10 => ref _e10, 11 => ref _e11,
        12 => ref _e12, 13 => ref _e13, 14 => ref _e14, 15 => ref _e15,
        16 => ref _e16, 17 => ref _e17, 18 => ref _e18, 19 => ref _e19,
        20 => ref _e20, 21 => ref _e21, 22 => ref _e22, 23 => ref _e23,
        24 => ref _e24, 25 => ref _e25, 26 => ref _e26, 27 => ref _e27,
        28 => ref _e28, 29 => ref _e29, 30 => ref _e30, 31 => ref _e31,
        _ => ref Unsafe.NullRef<PatternElement>()
    };
}
```

}

// ═══════════════════════════════════════════════════════════════════════════
// Span Extension Methods for Type-Safe Access
// ═══════════════════════════════════════════════════════════════════════════

public static class PatternSpanExtensions
{
/// <summary>
/// Enumerate only triple patterns
/// </summary>
public static TriplePatternEnumerator EnumerateTriples(this Span<PatternElement> elements)
=> new(elements);

```
/// <summary>
/// Enumerate only filter expressions
/// </summary>
public static FilterEnumerator EnumerateFilters(this Span<PatternElement> elements)
    => new(elements);

/// <summary>
/// Count elements of a specific kind
/// </summary>
public static int CountOfKind(this ReadOnlySpan<PatternElement> elements, PatternElementKind kind)
{
    int count = 0;
    foreach (ref readonly var e in elements)
    {
        if (e.Kind == kind) count++;
    }
    return count;
}

/// <summary>
/// Try to get element as triple pattern
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static bool TryAsTriple(this ref PatternElement element, out Term subject, out Term predicate, out Term obj)
{
    if (element.Kind == PatternElementKind.Triple || element.Kind == PatternElementKind.MinusTriple)
    {
        subject = element.Subject;
        predicate = element.Predicate;
        obj = element.Object;
        return true;
    }
    subject = default;
    predicate = default;
    obj = default;
    return false;
}

/// <summary>
/// Try to get element as filter
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static bool TryAsFilter(this ref PatternElement element, out int start, out int length)
{
    if (element.Kind == PatternElementKind.Filter)
    {
        start = element.FilterStart;
        length = element.FilterLength;
        return true;
    }
    start = 0;
    length = 0;
    return false;
}

/// <summary>
/// Get child elements for a GRAPH or EXISTS clause
/// </summary>
public static Span<PatternElement> GetChildren(this Span<PatternElement> elements, ref PatternElement header)
{
    return header.Kind switch
    {
        PatternElementKind.GraphClause => elements.Slice(header.GraphChildStart, header.GraphChildCount),
        PatternElementKind.Exists or PatternElementKind.NotExists => elements.Slice(header.ExistsChildStart, header.ExistsChildCount),
        _ => Span<PatternElement>.Empty
    };
}
```

}

/// <summary>
/// Zero-allocation enumerator for triple patterns only
/// </summary>
public ref struct TriplePatternEnumerator
{
private readonly Span<PatternElement> _elements;
private int _index;

```
public TriplePatternEnumerator(Span<PatternElement> elements)
{
    _elements = elements;
    _index = -1;
}

public bool MoveNext()
{
    while (++_index < _elements.Length)
    {
        var kind = _elements[_index].Kind;
        if (kind == PatternElementKind.Triple || kind == PatternElementKind.MinusTriple)
            return true;
    }
    return false;
}

public readonly ref PatternElement Current => ref _elements[_index];
public readonly bool IsMinus => _elements[_index].Kind == PatternElementKind.MinusTriple;

public TriplePatternEnumerator GetEnumerator() => this;
```

}

/// <summary>
/// Zero-allocation enumerator for filters only
/// </summary>
public ref struct FilterEnumerator
{
private readonly Span<PatternElement> _elements;
private int _index;

```
public FilterEnumerator(Span<PatternElement> elements)
{
    _elements = elements;
    _index = -1;
}

public bool MoveNext()
{
    while (++_index < _elements.Length)
    {
        if (_elements[_index].Kind == PatternElementKind.Filter)
            return true;
    }
    return false;
}

public readonly ref PatternElement Current => ref _elements[_index];

public FilterEnumerator GetEnumerator() => this;
```

}

// ═══════════════════════════════════════════════════════════════════════════
// Usage Example
// ═══════════════════════════════════════════════════════════════════════════

/*
// Before (original): ~9KB on stack
GraphPattern pattern = new();
pattern.AddPattern(new TriplePattern { … });
pattern.AddFilter(new FilterExpr { … });

// After (compact): ~2KB on stack, typically uses <500 bytes
CompactGraphPattern pattern = new();
pattern.AddTriple(subject, predicate, obj);
pattern.AddFilter(filterStart, filterLength);

// Type-safe iteration via span extensions:
var span = pattern.AsSpan();

// Iterate only triples
foreach (ref var element in span.EnumerateTriples())
{
element.TryAsTriple(out var s, out var p, out var o);
// process triple…
}

// Get children of a GRAPH clause
ref var graphHeader = ref span[0];
var children = span.GetChildren(ref graphHeader);

// Count specific types
int filterCount = span.CountOfKind(PatternElementKind.Filter);
*/

// ═══════════════════════════════════════════════════════════════════════════
// Alternative: Byte-level discrimination with raw Span<byte>
// (More memory-efficient but requires careful alignment)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Ultra-compact variant: variable-size elements in byte buffer.
/// Even smaller footprint but more complex to work with.
/// </summary>
public ref struct BytePackedPattern
{
private Span<byte> _buffer;
private int _offset;

```
public BytePackedPattern(Span<byte> buffer)
{
    _buffer = buffer;
    _offset = 0;
}

public void AddTriple(Term s, Term p, Term o)
{
    // [Kind:1][Subject:12][Predicate:12][Object:12] = 37 bytes
    const int TripleSize = 1 + 12 + 12 + 12;
    if (_offset + TripleSize > _buffer.Length) return;
    
    _buffer[_offset++] = (byte)PatternElementKind.Triple;
    WriteTerm(_buffer.Slice(_offset), s); _offset += 12;
    WriteTerm(_buffer.Slice(_offset), p); _offset += 12;
    WriteTerm(_buffer.Slice(_offset), o); _offset += 12;
}

public void AddFilter(int start, int length)
{
    // [Kind:1][Start:4][Length:4] = 9 bytes
    const int FilterSize = 1 + 4 + 4;
    if (_offset + FilterSize > _buffer.Length) return;
    
    _buffer[_offset++] = (byte)PatternElementKind.Filter;
    WriteInt(_buffer.Slice(_offset), start); _offset += 4;
    WriteInt(_buffer.Slice(_offset), length); _offset += 4;
}

private static void WriteTerm(Span<byte> dest, Term term)
{
    dest[0] = (byte)term.Type;
    WriteInt(dest.Slice(1), term.Start);
    WriteInt(dest.Slice(5), term.Length);
}

private static void WriteInt(Span<byte> dest, int value)
{
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dest, value);
}

// Reading requires walking the buffer since elements are variable size
public BytePackedEnumerator GetEnumerator() => new(_buffer.Slice(0, _offset));
```

}

public ref struct BytePackedEnumerator
{
private readonly ReadOnlySpan<byte> _buffer;
private int _offset;
private PatternElementKind _currentKind;
private int _currentStart;

```
public BytePackedEnumerator(ReadOnlySpan<byte> buffer)
{
    _buffer = buffer;
    _offset = 0;
    _currentKind = PatternElementKind.Empty;
    _currentStart = 0;
}

public bool MoveNext()
{
    if (_offset >= _buffer.Length) return false;
    
    _currentStart = _offset;
    _currentKind = (PatternElementKind)_buffer[_offset++];
    
    // Skip past element data based on kind
    _offset += _currentKind switch
    {
        PatternElementKind.Triple => 36,      // 3 Terms
        PatternElementKind.Filter => 8,        // 2 ints
        PatternElementKind.Bind => 16,         // 4 ints
        _ => 0
    };
    
    return true;
}

public readonly PatternElementKind CurrentKind => _currentKind;
public readonly ReadOnlySpan<byte> CurrentData => _buffer.Slice(_currentStart + 1);

public BytePackedEnumerator GetEnumerator() => this;
```

}