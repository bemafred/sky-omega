// SparqlResultFormat.cs
// SPARQL result format detection and content negotiation
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.IO;

namespace SkyOmega.Mercury.Sparql.Results;

/// <summary>
/// Supported SPARQL query result formats.
/// </summary>
public enum SparqlResultFormat
{
    /// <summary>Unknown or unsupported format.</summary>
    Unknown = 0,

    /// <summary>SPARQL Query Results JSON Format (application/sparql-results+json).</summary>
    Json,

    /// <summary>SPARQL Query Results XML Format (application/sparql-results+xml).</summary>
    Xml,

    /// <summary>SPARQL Query Results CSV Format (text/csv).</summary>
    Csv,

    /// <summary>SPARQL Query Results TSV Format (text/tab-separated-values).</summary>
    Tsv
}

/// <summary>
/// Content negotiation for SPARQL result formats.
/// Maps MIME types and file extensions to result formats.
/// </summary>
public static class SparqlResultFormatNegotiator
{
    /// <summary>
    /// Detect SPARQL result format from a MIME content type.
    /// </summary>
    /// <param name="contentType">The Content-Type or Accept header value.</param>
    /// <returns>The detected format, or Unknown if not recognized.</returns>
    public static SparqlResultFormat FromContentType(ReadOnlySpan<char> contentType)
    {
        if (contentType.IsEmpty)
            return SparqlResultFormat.Unknown;

        // Strip any parameters (e.g., "; charset=utf-8")
        var semicolonIndex = contentType.IndexOf(';');
        if (semicolonIndex >= 0)
            contentType = contentType.Slice(0, semicolonIndex);

        // Trim whitespace
        contentType = contentType.Trim();

        // Check known content types (case-insensitive)
        if (contentType.Equals("application/sparql-results+json".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("application/json".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Json;
        }

        if (contentType.Equals("application/sparql-results+xml".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("application/xml".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Xml;
        }

        if (contentType.Equals("text/csv".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Csv;
        }

        if (contentType.Equals("text/tab-separated-values".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("text/tsv".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Tsv;
        }

        return SparqlResultFormat.Unknown;
    }

    /// <summary>
    /// Detect SPARQL result format from a file extension.
    /// </summary>
    /// <param name="extension">File extension (with or without leading dot).</param>
    /// <returns>The detected format, or Unknown if not recognized.</returns>
    public static SparqlResultFormat FromExtension(ReadOnlySpan<char> extension)
    {
        if (extension.IsEmpty)
            return SparqlResultFormat.Unknown;

        // Skip leading dot if present
        if (extension[0] == '.')
            extension = extension.Slice(1);

        if (extension.Equals("json".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("srj".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Json;
        }

        if (extension.Equals("xml".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("srx".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Xml;
        }

        if (extension.Equals("csv".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Csv;
        }

        if (extension.Equals("tsv".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return SparqlResultFormat.Tsv;
        }

        return SparqlResultFormat.Unknown;
    }

    /// <summary>
    /// Get the primary MIME content type for a SPARQL result format.
    /// </summary>
    public static string GetContentType(SparqlResultFormat format) => format switch
    {
        SparqlResultFormat.Json => "application/sparql-results+json",
        SparqlResultFormat.Xml => "application/sparql-results+xml",
        SparqlResultFormat.Csv => "text/csv",
        SparqlResultFormat.Tsv => "text/tab-separated-values",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Get the standard file extension for a SPARQL result format.
    /// </summary>
    public static string GetExtension(SparqlResultFormat format) => format switch
    {
        SparqlResultFormat.Json => ".srj",
        SparqlResultFormat.Xml => ".srx",
        SparqlResultFormat.Csv => ".csv",
        SparqlResultFormat.Tsv => ".tsv",
        _ => ""
    };

    /// <summary>
    /// Parse an Accept header and return the preferred format.
    /// Supports quality values (q=) for content negotiation.
    /// </summary>
    /// <param name="acceptHeader">The Accept header value.</param>
    /// <param name="defaultFormat">Default format if none specified or recognized.</param>
    /// <returns>The preferred format based on the Accept header.</returns>
    public static SparqlResultFormat FromAcceptHeader(ReadOnlySpan<char> acceptHeader, SparqlResultFormat defaultFormat = SparqlResultFormat.Json)
    {
        if (acceptHeader.IsEmpty)
            return defaultFormat;

        // Handle wildcard
        if (acceptHeader.Equals("*/*".AsSpan(), StringComparison.Ordinal))
            return defaultFormat;

        SparqlResultFormat bestFormat = SparqlResultFormat.Unknown;
        double bestQuality = -1;

        // Split by comma and process each media type
        int start = 0;
        while (start < acceptHeader.Length)
        {
            // Find next comma or end
            int end = acceptHeader.Slice(start).IndexOf(',');
            if (end < 0)
                end = acceptHeader.Length - start;

            var part = acceptHeader.Slice(start, end).Trim();
            start += end + 1;

            if (part.IsEmpty)
                continue;

            // Extract media type and quality
            double quality = 1.0;
            var semicolonIndex = part.IndexOf(';');
            ReadOnlySpan<char> mediaType;

            if (semicolonIndex >= 0)
            {
                mediaType = part.Slice(0, semicolonIndex).Trim();
                var qualityPart = part.Slice(semicolonIndex + 1).Trim();

                // Look for q=
                if (qualityPart.StartsWith("q=".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    var qValue = qualityPart.Slice(2);
                    // Remove any additional parameters
                    var nextSemi = qValue.IndexOf(';');
                    if (nextSemi >= 0)
                        qValue = qValue.Slice(0, nextSemi);

                    if (double.TryParse(qValue.ToString(), out var q))
                        quality = q;
                }
            }
            else
            {
                mediaType = part;
            }

            var format = FromContentType(mediaType);
            if (format != SparqlResultFormat.Unknown && quality > bestQuality)
            {
                bestFormat = format;
                bestQuality = quality;
            }
        }

        return bestFormat != SparqlResultFormat.Unknown ? bestFormat : defaultFormat;
    }

    /// <summary>
    /// Try to detect format from content type first, then fall back to file extension.
    /// </summary>
    /// <param name="contentType">Content-Type header (may be null or empty).</param>
    /// <param name="path">File path (may be null or empty).</param>
    /// <param name="defaultFormat">Default format if detection fails.</param>
    /// <returns>The detected format.</returns>
    public static SparqlResultFormat Negotiate(string? contentType, string? path, SparqlResultFormat defaultFormat = SparqlResultFormat.Json)
    {
        // Try content type first
        if (!string.IsNullOrEmpty(contentType))
        {
            var format = FromContentType(contentType.AsSpan());
            if (format != SparqlResultFormat.Unknown)
                return format;
        }

        // Fall back to path extension
        if (!string.IsNullOrEmpty(path))
        {
            var lastDot = path.LastIndexOf('.');
            if (lastDot >= 0)
            {
                var format = FromExtension(path.AsSpan().Slice(lastDot));
                if (format != SparqlResultFormat.Unknown)
                    return format;
            }
        }

        return defaultFormat;
    }

    /// <summary>
    /// Create a result writer for the specified format.
    /// </summary>
    /// <param name="writer">The TextWriter to write to.</param>
    /// <param name="format">The result format.</param>
    /// <returns>A result writer for the format.</returns>
    /// <exception cref="NotSupportedException">If the format is unknown.</exception>
    public static IDisposable CreateWriter(TextWriter writer, SparqlResultFormat format)
    {
        return format switch
        {
            SparqlResultFormat.Json => new SparqlJsonResultWriter(writer),
            SparqlResultFormat.Xml => new SparqlXmlResultWriter(writer),
            SparqlResultFormat.Csv => new SparqlCsvResultWriter(writer),
            SparqlResultFormat.Tsv => new SparqlCsvResultWriter(writer, useTsv: true),
            _ => throw new NotSupportedException($"Unsupported SPARQL result format: {format}")
        };
    }

    /// <summary>
    /// Create a result writer based on Accept header content negotiation.
    /// </summary>
    /// <param name="writer">The TextWriter to write to.</param>
    /// <param name="acceptHeader">The Accept header for content negotiation.</param>
    /// <param name="defaultFormat">Default format if negotiation fails.</param>
    /// <returns>A result writer for the negotiated format.</returns>
    public static IDisposable CreateWriter(TextWriter writer, string? acceptHeader, SparqlResultFormat defaultFormat = SparqlResultFormat.Json)
    {
        var format = string.IsNullOrEmpty(acceptHeader)
            ? defaultFormat
            : FromAcceptHeader(acceptHeader.AsSpan(), defaultFormat);
        return CreateWriter(writer, format);
    }

    /// <summary>
    /// Create a result writer based on file path extension.
    /// </summary>
    /// <param name="writer">The TextWriter to write to.</param>
    /// <param name="path">File path for extension-based format detection.</param>
    /// <param name="defaultFormat">Default format if detection fails.</param>
    /// <returns>A result writer for the detected format.</returns>
    public static IDisposable CreateWriterFromPath(TextWriter writer, string path, SparqlResultFormat defaultFormat = SparqlResultFormat.Json)
    {
        var format = Negotiate(null, path, defaultFormat);
        return CreateWriter(writer, format);
    }

    /// <summary>
    /// Create a result parser for the specified format.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="format">The result format.</param>
    /// <returns>A result parser for the format.</returns>
    /// <exception cref="NotSupportedException">If the format is unknown.</exception>
    public static IDisposable CreateParser(Stream stream, SparqlResultFormat format)
    {
        return format switch
        {
            SparqlResultFormat.Json => new SparqlJsonResultParser(stream),
            SparqlResultFormat.Xml => new SparqlXmlResultParser(stream),
            SparqlResultFormat.Csv => new SparqlCsvResultParser(stream),
            SparqlResultFormat.Tsv => new SparqlCsvResultParser(stream, isTsv: true),
            _ => throw new NotSupportedException($"Unsupported SPARQL result format: {format}")
        };
    }

    /// <summary>
    /// Create a result parser based on Content-Type header.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="contentType">The Content-Type header.</param>
    /// <param name="defaultFormat">Default format if detection fails.</param>
    /// <returns>A result parser for the detected format.</returns>
    public static IDisposable CreateParser(Stream stream, string? contentType, SparqlResultFormat defaultFormat = SparqlResultFormat.Json)
    {
        var format = string.IsNullOrEmpty(contentType)
            ? defaultFormat
            : FromContentType(contentType.AsSpan());
        if (format == SparqlResultFormat.Unknown)
            format = defaultFormat;
        return CreateParser(stream, format);
    }

    /// <summary>
    /// Create a result parser based on file path extension.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="path">File path for extension-based format detection.</param>
    /// <param name="defaultFormat">Default format if detection fails.</param>
    /// <returns>A result parser for the detected format.</returns>
    public static IDisposable CreateParserFromPath(Stream stream, string path, SparqlResultFormat defaultFormat = SparqlResultFormat.Json)
    {
        var format = Negotiate(null, path, defaultFormat);
        return CreateParser(stream, format);
    }
}
