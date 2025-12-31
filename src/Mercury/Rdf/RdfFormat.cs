// RdfFormat.cs
// RDF format detection and content negotiation
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.IO;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// Supported RDF serialization formats.
/// </summary>
public enum RdfFormat
{
    /// <summary>Unknown or unsupported format.</summary>
    Unknown = 0,

    /// <summary>N-Triples format (application/n-triples).</summary>
    NTriples,

    /// <summary>Turtle format (text/turtle).</summary>
    Turtle,

    /// <summary>RDF/XML format (application/rdf+xml).</summary>
    RdfXml,

    /// <summary>N-Quads format (application/n-quads).</summary>
    NQuads,

    /// <summary>TriG format (application/trig).</summary>
    TriG,

    /// <summary>JSON-LD format (application/ld+json).</summary>
    JsonLd
}

/// <summary>
/// Content negotiation for RDF formats.
/// Maps MIME types and file extensions to RDF formats.
/// </summary>
public static class RdfFormatNegotiator
{
    /// <summary>
    /// Detect RDF format from a MIME content type.
    /// </summary>
    /// <param name="contentType">The Content-Type header value (may include parameters like charset).</param>
    /// <returns>The detected format, or Unknown if not recognized.</returns>
    public static RdfFormat FromContentType(ReadOnlySpan<char> contentType)
    {
        if (contentType.IsEmpty)
            return RdfFormat.Unknown;

        // Strip any parameters (e.g., "; charset=utf-8")
        var semicolonIndex = contentType.IndexOf(';');
        if (semicolonIndex >= 0)
            contentType = contentType.Slice(0, semicolonIndex);

        // Trim whitespace
        contentType = contentType.Trim();

        // Check known content types (case-insensitive)
        if (contentType.Equals("text/turtle".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("application/x-turtle".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.Turtle;
        }

        if (contentType.Equals("application/n-triples".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("text/plain".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.NTriples;
        }

        if (contentType.Equals("application/rdf+xml".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("application/xml".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("text/xml".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.RdfXml;
        }

        if (contentType.Equals("application/n-quads".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("text/x-nquads".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.NQuads;
        }

        if (contentType.Equals("application/trig".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("application/x-trig".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.TriG;
        }

        if (contentType.Equals("application/ld+json".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.JsonLd;
        }

        return RdfFormat.Unknown;
    }

    /// <summary>
    /// Detect RDF format from a file extension.
    /// </summary>
    /// <param name="extension">File extension (with or without leading dot).</param>
    /// <returns>The detected format, or Unknown if not recognized.</returns>
    public static RdfFormat FromExtension(ReadOnlySpan<char> extension)
    {
        if (extension.IsEmpty)
            return RdfFormat.Unknown;

        // Skip leading dot if present
        if (extension[0] == '.')
            extension = extension.Slice(1);

        if (extension.Equals("ttl".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("turtle".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.Turtle;
        }

        if (extension.Equals("nt".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("ntriples".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.NTriples;
        }

        if (extension.Equals("rdf".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("xml".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("rdfxml".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.RdfXml;
        }

        if (extension.Equals("nq".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("nquads".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.NQuads;
        }

        if (extension.Equals("trig".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.TriG;
        }

        if (extension.Equals("jsonld".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return RdfFormat.JsonLd;
        }

        return RdfFormat.Unknown;
    }

    /// <summary>
    /// Detect RDF format from a file path.
    /// </summary>
    /// <param name="path">File path or URL.</param>
    /// <returns>The detected format, or Unknown if not recognized.</returns>
    public static RdfFormat FromPath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
            return RdfFormat.Unknown;

        // Find the last dot for extension
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0 || lastDot == path.Length - 1)
            return RdfFormat.Unknown;

        // Handle query strings in URLs
        var queryIndex = path.LastIndexOf('?');
        ReadOnlySpan<char> extension;
        if (queryIndex > lastDot)
        {
            extension = path.Slice(lastDot + 1, queryIndex - lastDot - 1);
        }
        else
        {
            extension = path.Slice(lastDot + 1);
        }

        return FromExtension(extension);
    }

    /// <summary>
    /// Get the primary MIME content type for an RDF format.
    /// </summary>
    public static string GetContentType(RdfFormat format) => format switch
    {
        RdfFormat.NTriples => "application/n-triples",
        RdfFormat.Turtle => "text/turtle",
        RdfFormat.RdfXml => "application/rdf+xml",
        RdfFormat.NQuads => "application/n-quads",
        RdfFormat.TriG => "application/trig",
        RdfFormat.JsonLd => "application/ld+json",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Get the standard file extension for an RDF format.
    /// </summary>
    public static string GetExtension(RdfFormat format) => format switch
    {
        RdfFormat.NTriples => ".nt",
        RdfFormat.Turtle => ".ttl",
        RdfFormat.RdfXml => ".rdf",
        RdfFormat.NQuads => ".nq",
        RdfFormat.TriG => ".trig",
        RdfFormat.JsonLd => ".jsonld",
        _ => ""
    };

    /// <summary>
    /// Try to detect format from content type first, then fall back to path extension.
    /// </summary>
    /// <param name="contentType">Content-Type header (may be null or empty).</param>
    /// <param name="path">File path or URL (may be null or empty).</param>
    /// <returns>The detected format, or Unknown if not recognized.</returns>
    public static RdfFormat Negotiate(string? contentType, string? path)
    {
        // Try content type first
        if (!string.IsNullOrEmpty(contentType))
        {
            var format = FromContentType(contentType.AsSpan());
            if (format != RdfFormat.Unknown)
                return format;
        }

        // Fall back to path extension
        if (!string.IsNullOrEmpty(path))
        {
            return FromPath(path.AsSpan());
        }

        return RdfFormat.Unknown;
    }

    /// <summary>
    /// Create a parser for the specified stream based on format detection.
    /// </summary>
    /// <param name="stream">The stream to parse.</param>
    /// <param name="contentType">Content-Type header (may be null).</param>
    /// <param name="path">File path or URL for extension-based detection (may be null).</param>
    /// <param name="bufferManager">Buffer manager for allocations (null for default pooled manager).</param>
    /// <returns>A parser for the detected format.</returns>
    /// <exception cref="NotSupportedException">If the format cannot be detected.</exception>
    public static IDisposable CreateParser(Stream stream, string? contentType = null, string? path = null, IBufferManager? bufferManager = null)
    {
        var format = Negotiate(contentType, path);
        return CreateParser(stream, format, bufferManager);
    }

    /// <summary>
    /// Create a parser for the specified format.
    /// For quad formats (N-Quads, TriG), use the dedicated parsers directly.
    /// </summary>
    /// <param name="stream">The stream to parse.</param>
    /// <param name="format">The RDF format.</param>
    /// <param name="bufferManager">Buffer manager for allocations (null for default pooled manager).</param>
    /// <returns>A parser for the format.</returns>
    /// <exception cref="NotSupportedException">If the format is unknown or a quad format.</exception>
    public static IDisposable CreateParser(Stream stream, RdfFormat format, IBufferManager? bufferManager = null)
    {
        return format switch
        {
            RdfFormat.NTriples => new NTriplesStreamParser(stream, bufferManager: bufferManager),
            RdfFormat.Turtle => new TurtleStreamParser(stream, bufferManager: bufferManager),
            RdfFormat.RdfXml => new RdfXmlStreamParser(stream, bufferManager: bufferManager),
            RdfFormat.NQuads => throw new NotSupportedException("N-Quads is a quad format. Use NQuadsStreamParser directly."),
            RdfFormat.TriG => throw new NotSupportedException("TriG is a quad format. Use TriGStreamParser directly."),
            RdfFormat.JsonLd => throw new NotSupportedException("JSON-LD is a quad format. Use JsonLdStreamParser directly."),
            _ => throw new NotSupportedException($"Unsupported RDF format: {format}")
        };
    }

    /// <summary>
    /// Create a writer for the specified TextWriter based on format.
    /// For quad formats (N-Quads, TriG), use the dedicated writers directly.
    /// </summary>
    /// <param name="writer">The TextWriter to write to.</param>
    /// <param name="format">The RDF format.</param>
    /// <param name="bufferManager">Buffer manager for allocations (null for default pooled manager).</param>
    /// <returns>A writer for the format.</returns>
    /// <exception cref="NotSupportedException">If the format is unknown or a quad format.</exception>
    public static IDisposable CreateWriter(TextWriter writer, RdfFormat format, IBufferManager? bufferManager = null)
    {
        return format switch
        {
            RdfFormat.NTriples => new NTriplesStreamWriter(writer, bufferManager: bufferManager),
            RdfFormat.Turtle => new TurtleStreamWriter(writer, bufferManager: bufferManager),
            RdfFormat.RdfXml => new RdfXmlStreamWriter(writer, bufferManager: bufferManager),
            RdfFormat.NQuads => throw new NotSupportedException("N-Quads is a quad format. Use NQuadsStreamWriter directly."),
            RdfFormat.TriG => throw new NotSupportedException("TriG is a quad format. Use TriGStreamWriter directly."),
            RdfFormat.JsonLd => throw new NotSupportedException("JSON-LD is a quad format. Use JsonLdStreamWriter directly."),
            _ => throw new NotSupportedException($"Unsupported RDF format: {format}")
        };
    }

    /// <summary>
    /// Create a writer for the specified TextWriter based on format detection.
    /// </summary>
    /// <param name="writer">The TextWriter to write to.</param>
    /// <param name="contentType">Content-Type for format detection (may be null).</param>
    /// <param name="path">File path for extension-based detection (may be null).</param>
    /// <param name="defaultFormat">Default format if detection fails.</param>
    /// <param name="bufferManager">Buffer manager for allocations (null for default pooled manager).</param>
    /// <returns>A writer for the detected format.</returns>
    public static IDisposable CreateWriter(TextWriter writer, string? contentType = null, string? path = null, RdfFormat defaultFormat = RdfFormat.Turtle, IBufferManager? bufferManager = null)
    {
        var format = Negotiate(contentType, path);
        if (format == RdfFormat.Unknown)
            format = defaultFormat;
        return CreateWriter(writer, format, bufferManager);
    }
}
