using System;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Common administrative surface shared by every concrete quad-index implementation in
/// Mercury. Kept deliberately minimal for now: only the methods that <see cref="QuadStore"/>
/// already invokes polymorphically across all four of its indexes. Profile-specific API
/// (temporal queries, versioned updates, reference-only uniqueness) stays on the concrete
/// types — this interface grows when a second concrete implementation lands and surfaces
/// what's genuinely shared. See <see href="../docs/adrs/mercury/ADR-029-store-profiles.md">ADR-029</see>.
/// </summary>
internal interface IQuadIndex : IDisposable
{
    /// <summary>Number of quads currently stored in this index.</summary>
    long QuadCount { get; }

    /// <summary>Force all pending mmap writes to disk.</summary>
    void Flush();

    /// <summary>
    /// Reset the index to empty state. Must be called under the QuadStore writer lock.
    /// File size is preserved (memory mapping stays valid).
    /// </summary>
    void Clear();
}
