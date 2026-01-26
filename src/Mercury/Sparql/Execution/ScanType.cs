namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Discriminator for active scan type in QueryResults.
/// Used with StructLayout.Explicit to implement discriminated union pattern,
/// reducing stack usage from ~90KB to ~35KB (Phase 2) and eventually ~5KB (Phase 3).
///
/// See ADR-011 for details: docs/adrs/mercury/ADR-011-queryresults-stack-reduction.md
/// </summary>
internal enum ScanType : byte
{
    /// <summary>
    /// No scan active. Used for empty results or error states.
    /// </summary>
    None = 0,

    /// <summary>
    /// Single triple pattern scan using TriplePatternScan.
    /// Most common for simple queries like: SELECT * WHERE { ?s ?p ?o }
    /// </summary>
    Single = 1,

    /// <summary>
    /// Multi-pattern nested loop join using MultiPatternScan.
    /// Used for queries with 2+ patterns: SELECT * WHERE { ?s ?p ?o . ?o ?p2 ?o2 }
    /// </summary>
    Multi = 2,

    /// <summary>
    /// Subquery execution using SubQueryScan.
    /// Used for nested SELECT: SELECT * WHERE { { SELECT ?x WHERE { ... } } }
    /// </summary>
    SubQuery = 3,

    /// <summary>
    /// Default graph union scan using DefaultGraphUnionScan.
    /// Used for FROM semantics where multiple graphs form the default graph.
    /// </summary>
    DefaultGraphUnion = 4,

    /// <summary>
    /// Cross-graph multi-pattern scan using CrossGraphMultiPatternScan.
    /// Used for queries across named graphs.
    /// </summary>
    CrossGraph = 5,

    /// <summary>
    /// UNION branch using single pattern scan (TriplePatternScan).
    /// Active when iterating second branch of UNION with single pattern.
    /// </summary>
    UnionSingle = 6,

    /// <summary>
    /// UNION branch using multi-pattern scan (MultiPatternScan).
    /// Active when iterating second branch of UNION with multiple patterns.
    /// </summary>
    UnionMulti = 7,

    /// <summary>
    /// Pre-materialized results iteration.
    /// Used when results have been collected to a List for sorting, grouping,
    /// or to avoid stack overflow in complex query paths.
    /// </summary>
    Materialized = 8,

    /// <summary>
    /// Empty pattern with expressions only.
    /// Used for queries like: SELECT (1+1 AS ?x) WHERE { }
    /// Returns exactly one row with computed values.
    /// </summary>
    EmptyPattern = 9
}
