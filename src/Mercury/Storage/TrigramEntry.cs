using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// A single (trigram-hash, atom-id) entry produced during the trigram rebuild
/// path (ADR-032 Section 3). Sorted by Hash then AtomId so that all atoms
/// targeting a given posting list arrive contiguously and in order — the bulk
/// trigram append touches each posting list page once instead of re-reading
/// and re-writing it on every per-atom insert.
/// </summary>
/// <remarks>
/// 12 bytes total: 4 (Hash, unsigned uint32) + 8 (AtomId, signed long).
/// <see cref="StructLayoutAttribute"/> with <c>Pack = 1</c> and explicit field
/// order matters — the radix sort (<see cref="RadixSort.SortInPlace(System.Span{TrigramEntry}, System.Span{TrigramEntry})"/>)
/// indexes into the struct by absolute byte offset and depends on this layout.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TrigramEntry
{
    public uint Hash;   // bytes 0..3, unsigned (no signed-bias on radix MSB)
    public long AtomId; // bytes 4..11, signed (MSB at byte 11 needs signed-bias)
}
