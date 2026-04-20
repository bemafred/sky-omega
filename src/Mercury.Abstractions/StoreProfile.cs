namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Storage profile for a Mercury store. Chosen at creation, durable in store-schema.json,
/// and immutable for the lifetime of the store. Migrating between profiles requires a reload
/// from source. See <see href="../docs/adrs/mercury/ADR-029-store-profiles.md">ADR-029</see>.
/// </summary>
public enum StoreProfile
{
    /// <summary>
    /// Bitemporal, versioned, graph-aware. 88-byte B+Tree entries. Default — matches
    /// Mercury's original schema. Suited to cognitive memory, provenance-sensitive workloads.
    /// Indexes: GSPO, GPOS, GOSP, TGSP.
    /// </summary>
    Cognitive,

    /// <summary>
    /// Versioned, graph-aware, non-temporal. 64-byte entries. Suited to classic named-graph
    /// stores with soft-delete but no valid-time semantics. Indexes: GSPO, GPOS, GOSP, TGSP.
    /// </summary>
    Graph,

    /// <summary>
    /// Graph-aware, non-temporal, non-versioned. 32-byte entries. Suited to read-mostly
    /// reference dumps (Wikidata, DBpedia). Bulk-mutable, session-API immutable per ADR-029
    /// Decision 7. Indexes: GSPO, GPOS.
    /// </summary>
    Reference,

    /// <summary>
    /// Single-graph, non-temporal, non-versioned. 24-byte entries. Suited to Linked Data
    /// endpoints that don't need multi-graph semantics. Indexes: GSPO.
    /// </summary>
    Minimal
}
