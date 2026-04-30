using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Build a sorted-vocabulary <see cref="SortedAtomStore"/> on disk from a sequence of
/// UTF-8 byte strings. Phase 1B-2 ships an in-memory build path (sort + dedupe via
/// <see cref="SortedSet{T}"/>) — sufficient for tests and small-scale validation. The
/// external-merge-sort path (ADR-033's <c>ExternalSorter</c>, required for Wikidata's
/// 4 B-atom vocabulary) lands in Phase 1B-3 alongside QuadStore integration.
/// </summary>
internal static class SortedAtomStoreBuilder
{
    /// <summary>
    /// Build sorted vocabulary files at <paramref name="baseFilePath"/>. Input strings are
    /// deduplicated and sorted by UTF-8 byte order; the resulting files are
    /// <c>{base}.atoms</c> (concatenated bytes) and <c>{base}.offsets</c>
    /// (<c>long[N+1]</c> array). Returns the assigned atom IDs in input order — caller
    /// can use the result to remap any external references that point to the original
    /// insertion order.
    /// </summary>
    /// <param name="baseFilePath">Base path; <c>.atoms</c> and <c>.offsets</c> suffixes appended.</param>
    /// <param name="inputStrings">Source UTF-8 strings (any order, duplicates ok).</param>
    /// <returns>
    /// <see cref="BuildResult"/> with atom count, total bytes, and a per-input-index
    /// atom ID array (1-indexed). Index <c>i</c> of <see cref="BuildResult.AssignedIds"/>
    /// is the atom ID for the i-th input string.
    /// </returns>
    public static BuildResult Build(string baseFilePath, IList<byte[]> inputStrings)
    {
        if (baseFilePath is null) throw new ArgumentNullException(nameof(baseFilePath));
        if (inputStrings is null) throw new ArgumentNullException(nameof(inputStrings));

        // Collect distinct entries in sorted byte order. SortedSet handles both dedup
        // and ordering; comparator is byte-wise (matches SortedAtomStore's binary search).
        var sorted = new SortedSet<byte[]>(Utf8ByteComparer.Instance);
        for (int i = 0; i < inputStrings.Count; i++)
        {
            var s = inputStrings[i];
            if (s is not null && s.Length > 0)
                sorted.Add(s);
        }

        // Build a dictionary (string-bytes → assigned ID) so we can produce the per-input-index
        // remap. Same comparer for consistent equality semantics.
        var idByBytes = new Dictionary<byte[], long>(Utf8ByteComparer.Instance);
        long nextId = 1;
        foreach (var s in sorted) idByBytes[s] = nextId++;

        var assigned = new long[inputStrings.Count];
        for (int i = 0; i < inputStrings.Count; i++)
        {
            var s = inputStrings[i];
            assigned[i] = (s is null || s.Length == 0) ? 0 : idByBytes[s];
        }

        // Write files. Layout per SortedAtomStore: .atoms is concatenated bytes,
        // .offsets is long[N+1] with offsets[i] = start of atom (i+1), sentinel at end.
        var dataPath = baseFilePath + ".atoms";
        var offsetsPath = baseFilePath + ".offsets";

        long totalBytes = 0;
        using (var dataFs = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
        using (var offsetsFs = new FileStream(offsetsPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
        {
            Span<byte> offsetBuf = stackalloc byte[sizeof(long)];

            // offsets[0] = 0 (first atom starts at byte 0 of .atoms).
            BinaryPrimitives.WriteInt64LittleEndian(offsetBuf, 0);
            offsetsFs.Write(offsetBuf);

            foreach (var s in sorted)
            {
                dataFs.Write(s, 0, s.Length);
                totalBytes += s.Length;
                BinaryPrimitives.WriteInt64LittleEndian(offsetBuf, totalBytes);
                offsetsFs.Write(offsetBuf);
            }

            dataFs.Flush(flushToDisk: true);
            offsetsFs.Flush(flushToDisk: true);
        }

        return new BuildResult(sorted.Count, totalBytes, assigned);
    }

    /// <summary>Convenience overload taking string inputs; encodes to UTF-8 once.</summary>
    public static BuildResult Build(string baseFilePath, IList<string> inputStrings)
    {
        if (inputStrings is null) throw new ArgumentNullException(nameof(inputStrings));
        var asBytes = new byte[inputStrings.Count][];
        for (int i = 0; i < inputStrings.Count; i++)
            asBytes[i] = inputStrings[i] is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(inputStrings[i]);
        return Build(baseFilePath, asBytes);
    }

    /// <summary>
    /// Result of a build: counts plus the per-input-index atom-ID assignment table.
    /// </summary>
    /// <remarks>
    /// The in-memory <see cref="AssignedIds"/> array is the original (Phase 1B-5a) surface and
    /// remains the path for inputs that fit in RAM. For disk-backed builds (ADR-034 Phase 1B-5d,
    /// triggered by passing <c>useDiskBackedAssigned: true</c> to the external builder), the
    /// <see cref="AssignedIdsResolver"/> init-property carries an <see cref="IAssignedIds"/>
    /// streaming resolver instead. Exactly one of the two is populated per build:
    /// <see cref="AssignedIds"/> is empty for disk-backed builds, and <see cref="AssignedIdsResolver"/>
    /// is null for in-memory builds.
    /// </remarks>
    public sealed record BuildResult(long AtomCount, long DataBytes, long[] AssignedIds)
    {
        /// <summary>
        /// Disk-backed atom-ID resolver, populated only by external builds with
        /// <c>useDiskBackedAssigned: true</c>. When non-null, callers must use this in
        /// preference to the empty <see cref="AssignedIds"/> array. The resolver is disposable
        /// and is owned by the caller; typically <see cref="SortedAtomBulkBuilder"/> takes
        /// ownership and disposes it on its own <see cref="IDisposable.Dispose"/>.
        /// </summary>
        internal IAssignedIds? AssignedIdsResolver { get; init; }
    }

    private sealed class Utf8ByteComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        public static readonly Utf8ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y);
        }

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            // Simple FNV-1a — collision-safe enough for the dedup dictionary; the actual
            // sort ordering uses Compare which is byte-exact.
            unchecked
            {
                const int prime = 16777619;
                int hash = unchecked((int)2166136261);
                foreach (var b in obj)
                {
                    hash ^= b;
                    hash *= prime;
                }
                return hash;
            }
        }
    }
}
