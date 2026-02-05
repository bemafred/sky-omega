// OutputFormat.cs
// SPARQL result output formats

namespace SkyOmega.Mercury.Sparql.Tool;

/// <summary>
/// Output format for SPARQL SELECT query results.
/// </summary>
public enum OutputFormat
{
    /// <summary>Unknown or unspecified format.</summary>
    Unknown,

    /// <summary>JSON (application/sparql-results+json).</summary>
    Json,

    /// <summary>CSV (text/csv).</summary>
    Csv,

    /// <summary>TSV (text/tab-separated-values).</summary>
    Tsv,

    /// <summary>XML (application/sparql-results+xml).</summary>
    Xml
}
