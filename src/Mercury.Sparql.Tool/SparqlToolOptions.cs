// SparqlToolOptions.cs
// Strongly-typed options for SPARQL tool operations

using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Sparql.Tool;

/// <summary>
/// Options for SPARQL tool operations.
/// </summary>
public class SparqlToolOptions
{
    /// <summary>
    /// RDF file to load into the store.
    /// </summary>
    public string? LoadFile { get; set; }

    /// <summary>
    /// SPARQL query to execute.
    /// </summary>
    public string? Query { get; set; }

    /// <summary>
    /// File containing the SPARQL query to execute.
    /// </summary>
    public string? QueryFile { get; set; }

    /// <summary>
    /// Path to the QuadStore directory. If null, uses a temporary store.
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>
    /// SPARQL query to explain (show execution plan).
    /// </summary>
    public string? Explain { get; set; }

    /// <summary>
    /// Output format for SELECT query results.
    /// </summary>
    public OutputFormat Format { get; set; } = OutputFormat.Json;

    /// <summary>
    /// Output format for CONSTRUCT/DESCRIBE query results.
    /// </summary>
    public RdfFormat RdfOutputFormat { get; set; } = RdfFormat.NTriples;

    /// <summary>
    /// Whether to start interactive REPL mode.
    /// </summary>
    public bool Repl { get; set; }
}
