// QuadIndexCursor.cs
// The position of one scan in one quad index, and the row it is at (ADR-062 D3).
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// The position of one scan in one quad index, and the row the scan is at. One struct for every profile's index
/// (ADR-062 D3): the index moves it with <see cref="IQuadIndex.Advance"/>.
/// </summary>
/// <remarks>
/// <para>
/// A cursor has three parts. <b>The index</b> that moves it. <b>The state</b> of the scan, which belongs to the
/// index: each index class reads the same bytes as its own state struct (its page, its slot, its lower and upper
/// key, and whatever else its scan keeps), so no index's scan is written in terms of another's key. <b>The row</b>,
/// which the index writes when it accepts an entry.
/// </para>
/// <para>
/// A state struct holds no object reference, so the garbage collector has nothing to find in the state's bytes, and
/// a cursor is copied as plain data. A test asserts both for each index's state struct, and that it fits.
/// </para>
/// <para>
/// A cursor allocates nothing and is held by value. It is made by an index's own query methods and is at no row
/// until the first <see cref="MoveNext"/> returns true.
/// </para>
/// </remarks>
internal struct QuadIndexCursor
{
    /// <summary>The bytes an index has for its scan state. The widest state, the Cognitive index's, is 160.</summary>
    internal const int StateBytes = 160;

    [StructLayout(LayoutKind.Sequential, Size = StateBytes)]
    private struct StateBlock
    {
        private long _first;    // makes the block 8-byte aligned
    }

    private readonly IQuadIndex _index;
    private StateBlock _state;

    /// <summary>The graph of the row, as an atom; 0 is the default graph.</summary>
    public long Graph;

    /// <summary>The row's first, second and third dimension in the index's own order, as atoms.</summary>
    public long Primary, Secondary, Tertiary;

    /// <summary>
    /// When the row's version was memorized and when it was forgotten, in milliseconds since the epoch. Only an
    /// index that keeps a memory's two times writes them (the Cognitive profile's); <see cref="HasMemoryTimes"/>
    /// says whether this cursor's index does.
    /// </summary>
    public long MemorizedAt, ForgottenAt;

    /// <summary>
    /// Legacy (ADR-061 D11): the Cognitive entry's transaction time, its deleted mark and when it was marked.
    /// Carried for as long as the result type has them (ADR-061 Step 6 removes them). The Graph index writes its
    /// own deleted mark into <see cref="LegacyIsDeleted"/>.
    /// </summary>
    public long LegacyTransactionTime;

    /// <inheritdoc cref="LegacyTransactionTime"/>
    public long LegacyModifiedAt;

    /// <inheritdoc cref="LegacyTransactionTime"/>
    public bool LegacyIsDeleted;

    /// <summary>
    /// The row's version: the number of the fact's state, odd when asserted and even when retracted (ADR-064 M2).
    /// Only the Graph index writes it. The store's own operations read it; no read for a client returns it
    /// (ADR-064 D4).
    /// </summary>
    public int Version;

    /// <summary>
    /// Whether the index that moves this cursor writes <see cref="MemorizedAt"/> and <see cref="ForgottenAt"/>.
    /// The index says so when it makes the cursor. Read by the one place that still gives a result made-up times
    /// when the store keeps none (ADR-062 Step 5); it goes with those values (ADR-062 D1, D2).
    /// </summary>
    public readonly bool HasMemoryTimes;

    internal QuadIndexCursor(IQuadIndex index, bool hasMemoryTimes = false)
    {
        _index = index;
        _state = default;
        Graph = Primary = Secondary = Tertiary = 0;
        MemorizedAt = ForgottenAt = LegacyTransactionTime = LegacyModifiedAt = 0;
        LegacyIsDeleted = false;
        Version = 0;
        HasMemoryTimes = hasMemoryTimes;
    }

    /// <summary>
    /// The scan state, read as the state struct of the index that moves this cursor. Only that index calls this.
    /// </summary>
    [UnscopedRef]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TState State<TState>() where TState : unmanaged =>
        ref Unsafe.As<StateBlock, TState>(ref _state);

    /// <summary>Moves to the next row the scan accepts. False when there is none; the row is then not valid.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext() => _index.Advance(ref this);
}
