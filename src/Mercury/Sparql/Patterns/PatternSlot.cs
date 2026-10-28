using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;

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

    // Nestable group-like headers (ADR-045). Each carries a child-range at the GraphHeader
    // layout (ChildStartIndex@16 / ChildCount@20), so the tree is uniformly recursive.
    GroupHeader = 10,    // a nested group { ... }
    UnionHeader = 11,    // children are the union branches (each a GroupHeader)
    OptionalHeader = 12, // child is one GroupHeader
    MinusHeader = 13,    // child is one GroupHeader (block form of MINUS)
    SubSelectHeader = 14, // { SELECT … } — child is its WHERE group; carries its source span and where its modifiers start
    ServiceHeader = 15,   // SERVICE [SILENT] ep { … } — a leaf carrying the SERVICE source span (re-parsed on evaluation)
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
    /// <summary>A Triple slot's subject, predicate and object as a <see cref="TriplePattern"/> (no path).</summary>
    public TriplePattern ToTriplePattern() => new()
    {
        Subject = new Term { Type = SubjectType, Start = SubjectStart, Length = SubjectLength },
        Predicate = new Term { Type = PredicateType, Start = PredicateStart, Length = PredicateLength },
        Object = new Term { Type = ObjectType, Start = ObjectStart, Length = ObjectLength },
    };

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
    // Layout: [Kind:1][pad:3][Start:4][Length:4][ScopeDepth:4] = 16 bytes
    // ───────────────────────────────────────────────────────────────────────

    public ref int FilterStart => ref MemoryMarshal.AsRef<int>(_span.Slice(4, 4));
    public ref int FilterLength => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    /// <summary>
    /// Scope depth of this filter (0 = top level, 1 = first nested group, etc.)
    /// Used to exclude BIND variables from outer scopes per SPARQL scoping rules.
    /// </summary>
    public ref int FilterScopeDepth => ref MemoryMarshal.AsRef<int>(_span.Slice(12, 4));
    
    // ───────────────────────────────────────────────────────────────────────
    // Variant: Bind (Kind == Bind)
    // Layout: [Kind:1][pad:3][ExprStart:4][ExprLen:4][VarStart:4][VarLen:4][reserved:4][ScopeDepth:4] = 28 bytes
    // ───────────────────────────────────────────────────────────────────────

    public ref int BindExprStart => ref MemoryMarshal.AsRef<int>(_span.Slice(4, 4));
    public ref int BindExprLength => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    public ref int BindVarStart => ref MemoryMarshal.AsRef<int>(_span.Slice(12, 4));
    public ref int BindVarLength => ref MemoryMarshal.AsRef<int>(_span.Slice(16, 4));
    // Bytes 20-24 reserved (formerly AfterPatternIndex — the flat-slot BIND-ordering marker, removed
    // with the slot-operator engine; the tree orders BIND leaves structurally).
    /// <summary>
    /// Scope depth of this BIND (0 = top level, 1 = first nested group, etc.)
    /// Used to exclude this binding from filters in deeper scopes per SPARQL scoping rules.
    /// </summary>
    public ref int BindScopeDepth => ref MemoryMarshal.AsRef<int>(_span.Slice(24, 4));
    
    // ───────────────────────────────────────────────────────────────────────
    // Variant: GraphHeader (Kind == GraphHeader)
    // Layout: [Kind:1][pad:3][GraphTermType:1][pad:3][GraphStart:4][GraphLen:4][ChildStart:4][ChildCount:4] = 24 bytes
    // ───────────────────────────────────────────────────────────────────────
    
    public ref TermType GraphTermType => ref MemoryMarshal.AsRef<TermType>(_span.Slice(4, 1));
    public ref int GraphTermStart => ref MemoryMarshal.AsRef<int>(_span.Slice(8, 4));
    public ref int GraphTermLength => ref MemoryMarshal.AsRef<int>(_span.Slice(12, 4));
    public ref int ChildStartIndex => ref MemoryMarshal.AsRef<int>(_span.Slice(16, 4));
    public ref int ChildCount => ref MemoryMarshal.AsRef<int>(_span.Slice(20, 4));

    /// <summary>SubSelectHeader only: source offset of the sub-SELECT's SolutionModifier, just past its WHERE group.</summary>
    public ref int SubSelectModifiersStart => ref MemoryMarshal.AsRef<int>(_span.Slice(24, 4));
    
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
    public readonly bool IsGroupHeader => Kind == PatternKind.GroupHeader;
    public readonly bool IsUnionHeader => Kind == PatternKind.UnionHeader;
    public readonly bool IsOptionalHeader => Kind == PatternKind.OptionalHeader;
    public readonly bool IsMinusHeader => Kind == PatternKind.MinusHeader;

    /// <summary>
    /// True for headers that carry a child-range at the GraphHeader layout
    /// (ChildStartIndex@16 / ChildCount@20): GraphHeader, the ADR-045 nestable group headers and SubSelectHeader.
    /// (ExistsHeader uses a different offset and is excluded.) The one list every subtree walk reads.
    /// </summary>
    public readonly bool IsNestableGroup =>
        Kind == PatternKind.GraphHeader || Kind == PatternKind.GroupHeader ||
        Kind == PatternKind.UnionHeader || Kind == PatternKind.OptionalHeader ||
        Kind == PatternKind.MinusHeader || Kind == PatternKind.SubSelectHeader;

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
    /// <param name="start">Start offset of the filter expression in the source</param>
    /// <param name="length">Length of the filter expression</param>
    /// <param name="scopeDepth">Scope depth (0 = top level, 1 = first nested group, etc.)</param>
    public void AddFilter(int start, int length, int scopeDepth = 0)
    {
        var slot = AllocateSlot();
        slot.Kind = PatternKind.Filter;
        slot.FilterStart = start;
        slot.FilterLength = length;
        slot.FilterScopeDepth = scopeDepth;
    }
    
    /// <summary>
    /// Add a BIND expression
    /// </summary>
    /// <param name="exprStart">Start offset of the expression in the source</param>
    /// <param name="exprLen">Length of the expression</param>
    /// <param name="varStart">Start offset of the target variable (including ?)</param>
    /// <param name="varLen">Length of the variable name</param>
    /// <param name="scopeDepth">Scope depth (0 = top level, 1 = first nested group, etc.)</param>
    public void AddBind(int exprStart, int exprLen, int varStart, int varLen, int scopeDepth = 0)
    {
        var slot = AllocateSlot();
        slot.Kind = PatternKind.Bind;
        slot.BindExprStart = exprStart;
        slot.BindExprLength = exprLen;
        slot.BindVarStart = varStart;
        slot.BindVarLength = varLen;
        slot.BindScopeDepth = scopeDepth;
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
    /// Begin a sub-SELECT (ADR-055 D4): <c>SELECT … WHERE { … } modifiers</c> starting at <paramref name="sourceStart"/>.
    /// Its WHERE group follows as its one child, parsed once with every other group. Returns the header index.
    /// </summary>
    public int BeginSubSelect(int sourceStart)
    {
        int headerIndex = _count;
        var slot = AllocateSlot();
        slot.Kind = PatternKind.SubSelectHeader;
        slot.GraphTermStart = sourceStart;
        slot.ChildStartIndex = _count; // the WHERE group follows
        slot.ChildCount = 0;
        return headerIndex;
    }

    /// <summary>
    /// End a sub-SELECT: records its WHERE subtree, where its solution modifiers start, and its source end (exclusive).
    /// </summary>
    public void EndSubSelect(int headerIndex, int modifiersStart, int sourceEnd)
    {
        var header = this[headerIndex];
        header.ChildCount = _count - header.ChildStartIndex;
        header.SubSelectModifiersStart = modifiersStart;
        header.GraphTermLength = sourceEnd - header.GraphTermStart;
    }

    /// <summary>
    /// Add a SERVICE leaf (ADR-045): <c>SERVICE [SILENT] ep { … }</c>. The full SERVICE source span is carried on
    /// the slot (re-parsed on evaluation for the endpoint / SILENT / inner pattern, which is sent to the remote
    /// endpoint); it has no in-tree children. Returns the slot index.
    /// </summary>
    public int AddServiceHeader(int sourceStart, int sourceLength)
    {
        int headerIndex = _count;
        var slot = AllocateSlot();
        slot.Kind = PatternKind.ServiceHeader;
        slot.GraphTermStart = sourceStart;
        slot.GraphTermLength = sourceLength;
        return headerIndex;
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
    /// Begin a nestable group-like header (ADR-045): GroupHeader / UnionHeader / OptionalHeader / MinusHeader.
    /// Uses the same child-range layout as GraphHeader (ChildStartIndex@16 / ChildCount@20). Returns the header index.
    /// </summary>
    public int BeginGroupHeader(PatternKind kind)
    {
        int headerIndex = _count;
        var slot = AllocateSlot();
        slot.Kind = kind;
        slot.ChildStartIndex = _count; // children follow contiguously
        slot.ChildCount = 0;
        return headerIndex;
    }

    /// <summary>
    /// End a nestable group-like header; records the flattened descendant span as ChildCount.
    /// </summary>
    public void EndGroupHeader(int headerIndex)
    {
        var header = this[headerIndex];
        header.ChildCount = _count - header.ChildStartIndex;
    }

    /// <summary>
    /// Insert a new group-like header at <paramref name="subtreeStart"/>, wrapping the already-emitted,
    /// contiguous subtree <c>[subtreeStart, Count)</c> as its first children. Returns the new header's index
    /// (== <paramref name="subtreeStart"/>); the header is left open, so pair it with <see cref="EndGroupHeader"/>.
    /// </summary>
    /// <remarks>
    /// The append-only buffer requires a header to sit immediately before its contiguous children, but UNION's
    /// header kind is only known <em>after</em> its first branch has been parsed (ADR-045 recursive parser).
    /// This is the buffer equivalent of "collect children, then build their parent": it shifts the subtree up
    /// one slot (a memmove-safe overlapped copy) and bumps the absolute child-range index of every header inside
    /// it. Only headers with stored absolute child indices are bumped — group-like headers (ChildStartIndex) and
    /// EXISTS headers (ExistsChildStart); VALUES entries are positional and shift with the block.
    /// </remarks>
    public int WrapSubtree(int subtreeStart, PatternKind headerKind)
    {
        if (_count >= _capacity)
            throw new InvalidOperationException("PatternArray is full");

        int moved = _count - subtreeStart;

        // Shift [subtreeStart, _count) up by one slot. Span.CopyTo is memmove-safe for the overlap.
        _buffer.Slice(subtreeStart * PatternSlot.Size, moved * PatternSlot.Size)
            .CopyTo(_buffer.Slice((subtreeStart + 1) * PatternSlot.Size, moved * PatternSlot.Size));
        _count++;

        // Every header inside the shifted region pointed at an absolute child index >= subtreeStart; bump by one.
        for (int i = subtreeStart + 1; i < _count; i++)
        {
            var s = this[i];
            if (s.IsNestableGroup)
                s.ChildStartIndex += 1;
            else if (s.Kind is PatternKind.ExistsHeader or PatternKind.NotExistsHeader)
                s.ExistsChildStart += 1;
        }

        // Write the wrapping header into the freed slot; EndGroupHeader recomputes ChildCount once branches close.
        var header = new PatternSlot(_buffer.Slice(subtreeStart * PatternSlot.Size, PatternSlot.Size));
        header.Clear();
        header.Kind = headerKind;
        header.ChildStartIndex = subtreeStart + 1;
        header.ChildCount = moved;
        return subtreeStart;
    }

    /// <summary>
    /// Index one past the subtree rooted at <paramref name="index"/> (its next-sibling position).
    /// Group-like headers (<see cref="PatternSlot.IsNestableGroup"/>) span their flattened child-range; EXISTS headers
    /// span theirs; a VALUES header spans its trailing entries; every other slot is a single-slot leaf.
    /// </summary>
    public static int SubtreeEnd(Span<byte> buffer, int index)
    {
        var slot = new PatternSlot(buffer.Slice(index * PatternSlot.Size, PatternSlot.Size));
        if (slot.IsNestableGroup)
            return slot.ChildStartIndex + slot.ChildCount;
        switch (slot.Kind)
        {
            case PatternKind.ExistsHeader:
            case PatternKind.NotExistsHeader:
                return slot.ExistsChildStart + slot.ExistsChildCount;
            case PatternKind.ValuesHeader:
                return index + 1 + slot.ValuesEntryCount;
            default:
                return index + 1;
        }
    }

    /// <summary>
    /// Enumerate the DIRECT children of a group-like header, skipping nested subtrees (children are stored
    /// contiguously and ChildCount is the flattened span). The uniform-recursion walk seam (ADR-045).
    /// </summary>
    public DirectChildEnumerator EnumerateDirectChildren(int headerIndex)
    {
        var header = this[headerIndex];
        // EXISTS/NOT EXISTS headers carry their child-range at a different offset (ExistsChildStart@4) than the
        // nestable group headers (ChildStartIndex@16); dispatch so the uniform walk handles both.
        int start, count;
        if (header.Kind == PatternKind.ExistsHeader || header.Kind == PatternKind.NotExistsHeader)
        {
            start = header.ExistsChildStart;
            count = header.ExistsChildCount;
        }
        else
        {
            start = header.ChildStartIndex;
            count = header.ChildCount;
        }
        return new DirectChildEnumerator(_buffer, start, start + count);
    }

    /// <summary>
    /// Get child patterns for a header slot
    /// </summary>
    public PatternArraySlice GetChildren(int headerIndex)
    {
        var header = this[headerIndex];
        if (header.IsNestableGroup)
            return new PatternArraySlice(_buffer, header.ChildStartIndex, header.ChildCount);
        return header.Kind is PatternKind.ExistsHeader or PatternKind.NotExistsHeader
            ? new PatternArraySlice(_buffer, header.ExistsChildStart, header.ExistsChildCount)
            : default;
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
    /// Add a VALUES leaf carrying the full <c>VALUES … { … }</c> source span (ADR-045 recursive parser), read on
    /// evaluation with SparqlParser.DataBlockReader, so single- AND multi-variable forms are one path. The span is
    /// stored in the ValuesVar fields with zero trailing entries (so SubtreeEnd treats it as a single-slot leaf).
    /// </summary>
    public int AddValuesBlock(int sourceStart, int sourceLength)
    {
        int headerIndex = _count;
        var slot = AllocateSlot();
        slot.Kind = PatternKind.ValuesHeader;
        slot.ValuesVarStart = sourceStart;
        slot.ValuesVarLength = sourceLength;
        slot.ValuesEntryCount = 0;
        return headerIndex;
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
/// Enumerates the DIRECT children of a group-like header, skipping nested subtrees via
/// <see cref="PatternArray.SubtreeEnd"/>. Children are stored contiguously and a header's ChildCount is the
/// flattened descendant span, so a naive linear walk would mistake a nested header's children for siblings.
/// </summary>
internal ref struct DirectChildEnumerator
{
    private readonly Span<byte> _buffer;
    private readonly int _end;   // exclusive end of the child-range (absolute slot index)
    private int _pos;            // next direct child to yield (absolute slot index)
    private int _current;        // current direct child (absolute slot index)

    public DirectChildEnumerator(Span<byte> buffer, int start, int end)
    {
        _buffer = buffer;
        _end = end;
        _pos = start;
        _current = -1;
    }

    public bool MoveNext()
    {
        if (_pos >= _end) return false;
        _current = _pos;
        _pos = PatternArray.SubtreeEnd(_buffer, _pos); // skip this child's whole subtree
        return true;
    }

    public readonly PatternSlot Current => new(_buffer.Slice(_current * PatternSlot.Size, PatternSlot.Size));
    public readonly int CurrentIndex => _current;
    public DirectChildEnumerator GetEnumerator() => this;
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
// QueryBuffer - the parsed query's non-WHERE parts, on the heap
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// The parts of a parsed query that execution reads besides its WHERE: query type, projection, solution modifiers,
/// dataset, prologue, CONSTRUCT template, trailing VALUES and temporal clause. Kept on the heap so executors and
/// result presenters don't carry the large <c>Query</c> struct (ADR-009). The WHERE itself is parsed into a pattern
/// tree from <see cref="WhereGroupStart"/> (ADR-055 D5).
/// </summary>
internal sealed class QueryBuffer
{
    // Query metadata (lightweight - no large inline storage)
    public QueryType Type { get; set; }
    public bool SelectDistinct { get; set; }
    public bool SelectAll { get; set; }
    public LimitOffsetClause LimitOffset { get; set; }

    /// <summary>Source offset of the WHERE group's '{' (<see cref="WhereClause.GroupStart"/>); -1 without one.</summary>
    public int WhereGroupStart { get; set; } = -1;

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

    // HAVING clause (expression offsets into source)
    public int HavingStart { get; set; }
    public int HavingLength { get; set; }

    // DESCRIBE's targets
    public DescribeClause Describe { get; set; }

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

    // Post-query VALUES clause (appears after WHERE clause, not inline)
    // Stored separately from inline VALUES (which goes into pattern slots)
    public ValuesClause PostQueryValues { get; set; }
    public bool HasPostQueryValues => PostQueryValues.IsPresent;

    public bool HasHaving => HavingLength > 0;
    public bool HasConstruct => ConstructPatterns != null && ConstructPatterns.Length > 0;

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
    /// <summary>
    /// True if there are real aggregate functions (COUNT, SUM, AVG, etc.).
    /// Non-aggregate computed expressions do not count.
    /// </summary>
    public bool HasRealAggregates { get; init; }
    public AggregateEntry[]? Aggregates { get; init; }

    /// <summary>
    /// Projected variables (start, length pairs) for DISTINCT computation.
    /// </summary>
    public (int Start, int Length)[]? ProjectedVariables { get; init; }

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

        // Copy projected variables for DISTINCT computation
        (int, int)[]? projVars = null;
        if (clause.HasProjectedVariables)
        {
            projVars = new (int, int)[clause.ProjectedVariableCount];
            for (int i = 0; i < clause.ProjectedVariableCount; i++)
            {
                projVars[i] = clause.GetProjectedVariable(i);
            }
        }

        return new SelectClauseData
        {
            Distinct = clause.Distinct,
            Reduced = clause.Reduced,
            SelectAll = clause.SelectAll,
            HasAggregates = clause.HasAggregates,
            HasRealAggregates = clause.HasRealAggregates,
            Aggregates = aggs,
            ProjectedVariables = projVars
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

        // Restore projected variables
        if (ProjectedVariables != null)
        {
            foreach (var (start, length) in ProjectedVariables)
            {
                clause.AddProjectedVariable(start, length);
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
        var buffer = new QueryBuffer();
        PopulateBuffer(buffer, in query, source);
        return buffer;
    }

    private static void PopulateBuffer(QueryBuffer buffer, in Query query, ReadOnlySpan<char> source)
    {
        // Copy metadata
        buffer.Type = query.Type;
        buffer.SelectDistinct = query.SelectClause.Distinct || query.SelectClause.Reduced;
        buffer.SelectAll = query.SelectClause.SelectAll;
        buffer.LimitOffset = query.SolutionModifier.LimitOffset;
        buffer.WhereGroupStart = query.WhereClause.GroupStart;
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

        // Copy HAVING clause (ADR-009 Phase 2)
        buffer.HavingStart = query.SolutionModifier.Having.ExpressionStart;
        buffer.HavingLength = query.SolutionModifier.Having.ExpressionLength;

        // Copy DESCRIBE metadata
        buffer.Describe = query.Describe;

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

        // Copy post-query VALUES clause (if present)
        if (query.Values.IsPresent)
        {
            buffer.PostQueryValues = query.Values;
        }

    }
}
