using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Two-pass bulk builder for a Reference-profile <see cref="SortedAtomStore"/>. ADR-034
/// Phase 1B-5a. Accepts a stream of (G, S, P, O) UTF-8 byte spans, defers atom-ID
/// assignment until the full vocabulary is sorted, then yields per-triple resolved IDs
/// in input order so the caller can build <c>ReferenceKey</c> entries and feed them to
/// the GSPO external sorter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two-pass design.</b> The parser-side flow can't synthesize atom IDs synchronously
/// because <see cref="SortedAtomStore"/> assigns dense IDs in alphabetical order — the ID
/// for a string is unknowable until the entire vocabulary is sorted. So this builder
/// buffers atom strings during ingest and runs the assignment as a deferred merge:
/// </para>
/// <list type="number">
///   <item>
///     During <see cref="AddTriple"/>: write the four UTF-8 byte sequences to an
///     in-memory buffer in input order. Each string's "input index" is implicitly its
///     position in the flattened (G0, S0, P0, O0, G1, S1, ...) sequence.
///   </item>
///   <item>
///     <see cref="Finalize"/> hands the flattened buffer to
///     <see cref="SortedAtomStoreExternalBuilder.BuildExternal"/> which writes
///     <c>{base}.atoms</c> + <c>{base}.offsets</c> and returns
///     <see cref="SortedAtomStoreBuilder.BuildResult.AssignedIds"/> — a per-input-index
///     array mapping every atom occurrence to its dense assigned ID.
///   </item>
///   <item>
///     <see cref="EnumerateResolved"/> walks <c>AssignedIds</c> in triple order, yielding
///     <c>(GraphId, SubjectId, PredicateId, ObjectId)</c> tuples for the caller to push
///     into the GSPO external sorter (ADR-033's existing path).
///   </item>
/// </list>
/// <para>
/// <b>Scale.</b> Phase 1B-5a buffers all atom strings + the full <c>AssignedIds</c> array
/// in memory. For 1 M triples this is ~80 MB strings + 32 MB IDs = 112 MB — fine. For
/// 100 M triples it is 8 GB + 3.2 GB = 11.2 GB — manageable on a 128 GB host. For 21.3 B
/// triples (full Wikidata) the AssignedIds array alone is 680 GB — needs disk-backing,
/// addressed in Phase 1B-5d. The architecture is right; the scaling fix is mechanical.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The builder owns its input buffer and a temp directory for the
/// external merge sort. <see cref="Dispose"/> drops both. The output files
/// (<c>{base}.atoms</c>, <c>{base}.offsets</c>) are durable on <see cref="Finalize"/>
/// completion; the caller then constructs a <see cref="SortedAtomStore"/> over them.
/// </para>
/// </remarks>
internal sealed class SortedAtomBulkBuilder : IDisposable
{
    private readonly string _baseAtomPath;
    private readonly string _tempDir;
    private readonly bool _ownsTempDir;
    private readonly bool _useDiskBackedAssigned;

    // Flat input buffer: every atom occurrence in input order. Index 4*N+0 is the graph
    // of triple N, +1 subject, +2 predicate, +3 object. Empty entries (e.g., default
    // graph) are stored as zero-length byte[] so triple indexing stays simple.
    private readonly List<byte[]> _atomOccurrences = new();
    private long _tripleCount;

    private SortedAtomStoreBuilder.BuildResult? _buildResult;
    private bool _finalized;
    private bool _disposed;

    public SortedAtomBulkBuilder(string baseAtomPath, string? tempDir = null, bool useDiskBackedAssigned = false)
    {
        if (baseAtomPath is null) throw new ArgumentNullException(nameof(baseAtomPath));
        _baseAtomPath = baseAtomPath;
        _useDiskBackedAssigned = useDiskBackedAssigned;
        if (tempDir is null)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "sorted-atom-bulk-" + Guid.NewGuid().ToString("N"));
            _ownsTempDir = true;
        }
        else
        {
            _tempDir = tempDir;
            _ownsTempDir = false;
        }
        Directory.CreateDirectory(_tempDir);
    }

    public long TripleCount => _tripleCount;

    /// <summary>
    /// Add a triple to the build queue. Strings are buffered; atom IDs are not yet known.
    /// Empty <paramref name="graph"/> stores as a zero-length entry which becomes atom ID 0
    /// (the "no atom" sentinel) in <see cref="EnumerateResolved"/>.
    /// </summary>
    public void AddTriple(
        ReadOnlySpan<byte> graph,
        ReadOnlySpan<byte> subject,
        ReadOnlySpan<byte> predicate,
        ReadOnlySpan<byte> @object)
    {
        ThrowIfDisposed();
        ThrowIfFinalized();
        _atomOccurrences.Add(graph.IsEmpty ? Array.Empty<byte>() : graph.ToArray());
        _atomOccurrences.Add(subject.ToArray());
        _atomOccurrences.Add(predicate.ToArray());
        _atomOccurrences.Add(@object.ToArray());
        _tripleCount++;
    }

    /// <summary>UTF-8 convenience overload for char-based callers.</summary>
    public void AddTriple(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        // Encode each span once. Stack-buffer for typical sizes; heap-fallback for outliers.
        Span<byte> stack = stackalloc byte[2048];
        AddTriple(EncodeUtf8(graph, stack), EncodeUtf8(subject, stack), EncodeUtf8(predicate, stack), EncodeUtf8(@object, stack));
    }

    private static byte[] EncodeUtf8(ReadOnlySpan<char> chars, Span<byte> _)
    {
        // The stack span isn't actually usable across consecutive calls (it would be reused),
        // so allocate fresh byte[] per atom. Phase 1B-5a's in-memory model already heap-
        // allocates; the per-atom byte[] is part of that footprint, not extra cost.
        if (chars.IsEmpty) return Array.Empty<byte>();
        int n = Encoding.UTF8.GetByteCount(chars);
        var buf = new byte[n];
        Encoding.UTF8.GetBytes(chars, buf);
        return buf;
    }

    /// <summary>
    /// Sort the buffered vocabulary and write the SortedAtomStore files. Subsequent calls
    /// to <see cref="EnumerateResolved"/> stream the per-triple resolved atom IDs in input
    /// order. Returns the build result for diagnostics (atom count + total bytes).
    /// </summary>
    public SortedAtomStoreBuilder.BuildResult Finalize()
    {
        ThrowIfDisposed();
        if (_finalized) return _buildResult!;
        _buildResult = SortedAtomStoreExternalBuilder.BuildExternal(
            _baseAtomPath, _atomOccurrences, tempDir: _tempDir,
            useDiskBackedAssigned: _useDiskBackedAssigned);
        _finalized = true;
        return _buildResult;
    }

    /// <summary>
    /// Stream resolved per-triple atom IDs in original input order. <see cref="Finalize"/>
    /// must have been called. Each yielded tuple corresponds to one <see cref="AddTriple"/>
    /// call: <c>(GraphId, SubjectId, PredicateId, ObjectId)</c>. Empty atom occurrences
    /// (e.g. the default graph) yield 0 — the sentinel.
    /// </summary>
    public IEnumerable<(long GraphId, long SubjectId, long PredicateId, long ObjectId)> EnumerateResolved()
    {
        ThrowIfDisposed();
        if (!_finalized)
            throw new InvalidOperationException("EnumerateResolved called before Finalize.");

        // Disk-backed path streams resolved IDs in InputIdx order (0, 1, 2, ...) from the
        // ExternalSorter; in-memory path indexes the long[] directly. Both yield identical
        // (G, S, P, O) tuples per triple in input order.
        if (_buildResult!.AssignedIdsResolver is { } resolver)
        {
            using var reader = resolver.GetReader();
            for (long t = 0; t < _tripleCount; t++)
            {
                if (!reader.TryReadNext(out var g) ||
                    !reader.TryReadNext(out var s) ||
                    !reader.TryReadNext(out var p) ||
                    !reader.TryReadNext(out var o))
                {
                    throw new InvalidOperationException(
                        $"AssignedIdsResolver drained early at triple {t}/{_tripleCount}.");
                }
                yield return (g, s, p, o);
            }
        }
        else
        {
            var ids = _buildResult!.AssignedIds;
            for (long t = 0; t < _tripleCount; t++)
            {
                int baseIdx = checked((int)(t * 4));
                yield return (ids[baseIdx], ids[baseIdx + 1], ids[baseIdx + 2], ids[baseIdx + 3]);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Disk-backed AssignedIds owns an ExternalSorter with its own tempDir under _tempDir;
        // dispose it first so its tempDir is cleaned before _tempDir's recursive delete.
        _buildResult?.AssignedIdsResolver?.Dispose();

        if (_ownsTempDir && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SortedAtomBulkBuilder));
    }

    private void ThrowIfFinalized()
    {
        if (_finalized) throw new InvalidOperationException("Builder is finalized; AddTriple is no longer accepted.");
    }
}
