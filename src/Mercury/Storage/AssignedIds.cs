using System;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// 16-byte resolution record emitted during the SortedAtomStore vocabulary merge:
/// a flat-input-index slot and the dense atom ID assigned to its string.
/// Sorted by <see cref="InputIdx"/> so consumers can stream atom IDs in triple
/// input order. ADR-034 Phase 1B-5d.
/// </summary>
/// <remarks>
/// Sort key is <see cref="InputIdx"/> only; <see cref="AtomId"/> is payload.
/// <c>InputIdx</c> at offset 0..7, <c>AtomId</c> at offset 8..15.
/// <see cref="RadixSort.SortInPlace(Span{ResolveRecord}, Span{ResolveRecord})"/>
/// runs 8 LSD passes over the InputIdx field.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
internal readonly struct ResolveRecord : IComparable<ResolveRecord>
{
    public readonly long InputIdx;
    public readonly long AtomId;

    public ResolveRecord(long inputIdx, long atomId)
    {
        InputIdx = inputIdx;
        AtomId = atomId;
    }

    public int CompareTo(ResolveRecord other) => InputIdx.CompareTo(other.InputIdx);
}

/// <summary>
/// Per-input-index atom ID resolver. Backs <see cref="SortedAtomStoreBuilder.BuildResult"/>
/// for inputs that exceed the in-memory <c>long[]</c> ceiling. Disposed by the
/// owning bulk builder; reading is single-pass via <see cref="GetReader"/>.
/// ADR-034 Phase 1B-5d.
/// </summary>
internal interface IAssignedIds : IDisposable
{
    /// <summary>Total number of (inputIdx, atomId) records expected; equals the input occurrence count.</summary>
    long ExpectedCount { get; }

    /// <summary>
    /// Acquire a single-pass reader. The reader yields atom IDs in InputIdx order
    /// (0, 1, 2, ..., ExpectedCount-1). Caller must dispose.
    /// </summary>
    IAssignedIdReader GetReader();
}

/// <summary>Single-pass reader over an <see cref="IAssignedIds"/>.</summary>
internal interface IAssignedIdReader : IDisposable
{
    /// <summary>
    /// Read the next atom ID in input order. Returns false at end of stream.
    /// Caller invokes exactly <see cref="IAssignedIds.ExpectedCount"/> times in
    /// the success path.
    /// </summary>
    bool TryReadNext(out long atomId);
}

/// <summary>
/// Disk-backed atom-ID resolver wrapping an
/// <see cref="ExternalSorter{ResolveRecord, ResolveRecordChunkSorter}"/>. The
/// merge pass of <see cref="SortedAtomStoreExternalBuilder"/> emits one
/// <see cref="ResolveRecord"/> per input occurrence (including empty-graph
/// sentinels with AtomId 0); after <see cref="ExternalSorter{T,TSorter}.Complete"/>
/// the records drain in InputIdx order. Memory ceiling: the sorter's chunk
/// buffer (default 16 M records × 16 B = 256 MB), independent of input scale.
/// </summary>
internal sealed class DiskBackedAssignedIds : IAssignedIds
{
    private readonly ExternalSorter<ResolveRecord, ResolveRecordChunkSorter> _sorter;
    private readonly long _expectedCount;
    private bool _disposed;

    public DiskBackedAssignedIds(
        ExternalSorter<ResolveRecord, ResolveRecordChunkSorter> sorter,
        long expectedCount)
    {
        _sorter = sorter ?? throw new ArgumentNullException(nameof(sorter));
        _expectedCount = expectedCount;
    }

    public long ExpectedCount => _expectedCount;

    public IAssignedIdReader GetReader()
    {
        ThrowIfDisposed();
        return new DiskBackedReader(_sorter, _expectedCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sorter.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DiskBackedAssignedIds));
    }

    private sealed class DiskBackedReader : IAssignedIdReader
    {
        private readonly ExternalSorter<ResolveRecord, ResolveRecordChunkSorter> _sorter;
        private readonly long _expectedCount;
        private long _expectedIdx;

        public DiskBackedReader(
            ExternalSorter<ResolveRecord, ResolveRecordChunkSorter> sorter,
            long expectedCount)
        {
            _sorter = sorter;
            _expectedCount = expectedCount;
        }

        public bool TryReadNext(out long atomId)
        {
            if (_sorter.TryDrainNext(out var rec))
            {
                if (rec.InputIdx != _expectedIdx)
                    throw new InvalidOperationException(
                        $"DiskBackedAssignedIds dense-coverage violation: expected InputIdx {_expectedIdx}, got {rec.InputIdx}. " +
                        "All input occurrences (including empty-graph sentinels) must be emitted to the resolver.");
                atomId = rec.AtomId;
                _expectedIdx++;
                return true;
            }
            if (_expectedIdx != _expectedCount)
                throw new InvalidOperationException(
                    $"DiskBackedAssignedIds drained early: read {_expectedIdx} of {_expectedCount} expected records.");
            atomId = 0;
            return false;
        }

        public void Dispose() { /* sorter is owned by DiskBackedAssignedIds */ }
    }
}

/// <summary>
/// In-memory atom-ID resolver wrapping the existing <c>long[]</c> array. Kept for
/// the in-memory <see cref="SortedAtomStoreBuilder"/> path and the small-scale
/// <see cref="SortedAtomStoreExternalBuilder"/> path where memory cost is acceptable.
/// </summary>
internal sealed class InMemoryAssignedIds : IAssignedIds
{
    private readonly long[] _ids;

    public InMemoryAssignedIds(long[] ids)
    {
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    public long ExpectedCount => _ids.LongLength;

    public IAssignedIdReader GetReader() => new InMemoryReader(_ids);

    public void Dispose() { /* nothing to dispose */ }

    private sealed class InMemoryReader : IAssignedIdReader
    {
        private readonly long[] _ids;
        private long _pos;

        public InMemoryReader(long[] ids) => _ids = ids;

        public bool TryReadNext(out long atomId)
        {
            if (_pos >= _ids.LongLength)
            {
                atomId = 0;
                return false;
            }
            atomId = _ids[_pos++];
            return true;
        }

        public void Dispose() { }
    }
}
