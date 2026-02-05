// TurtleToolOptions.cs
// Strongly-typed options for Turtle tool operations

using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Turtle.Tool;

/// <summary>
/// Options for Turtle tool operations.
/// </summary>
public class TurtleToolOptions
{
    /// <summary>
    /// Input Turtle file path.
    /// </summary>
    public string? InputFile { get; set; }

    /// <summary>
    /// Output file path for conversion.
    /// </summary>
    public string? OutputFile { get; set; }

    /// <summary>
    /// Output format for conversion.
    /// </summary>
    public RdfFormat OutputFormat { get; set; } = RdfFormat.Unknown;

    /// <summary>
    /// Path to the QuadStore directory for loading.
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>
    /// Whether to validate syntax only.
    /// </summary>
    public bool Validate { get; set; }

    /// <summary>
    /// Whether to show statistics.
    /// </summary>
    public bool Stats { get; set; }

    /// <summary>
    /// Whether to run performance benchmark.
    /// </summary>
    public bool Benchmark { get; set; }

    /// <summary>
    /// Number of triples for benchmark (default: 10000).
    /// </summary>
    public int TripleCount { get; set; } = 10_000;
}
