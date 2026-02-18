using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.JsonLd;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.TriG;

namespace SkyOmega.Mercury;

/// <summary>
/// Handler for zero-allocation triple parsing via <see cref="RdfEngine"/>.
/// Receives spans that are valid only during the callback invocation.
/// </summary>
public delegate void RdfTripleHandler(
    ReadOnlySpan<char> subject,
    ReadOnlySpan<char> predicate,
    ReadOnlySpan<char> obj);

/// <summary>
/// Handler for zero-allocation quad parsing via <see cref="RdfEngine"/>.
/// Receives spans that are valid only during the callback invocation.
/// Graph is empty for the default graph.
/// </summary>
public delegate void RdfQuadHandler(
    ReadOnlySpan<char> subject,
    ReadOnlySpan<char> predicate,
    ReadOnlySpan<char> obj,
    ReadOnlySpan<char> graph);

/// <summary>
/// Facade for RDF parsing, writing, loading, and content negotiation.
/// Encapsulates the six-way format switch for parsers and writers,
/// batch loading into a QuadStore, and format detection.
/// </summary>
public static class RdfEngine
{
    #region Content Negotiation

    /// <summary>
    /// Determine the RDF format from a MIME content type string.
    /// </summary>
    public static RdfFormat DetermineFormat(ReadOnlySpan<char> contentType)
        => RdfFormatNegotiator.FromContentType(contentType);

    /// <summary>
    /// Negotiate the best RDF format from an Accept header.
    /// </summary>
    public static RdfFormat NegotiateFromAccept(ReadOnlySpan<char> acceptHeader, RdfFormat defaultFormat = RdfFormat.Turtle)
        => RdfFormatNegotiator.FromAcceptHeader(acceptHeader, defaultFormat);

    /// <summary>
    /// Get the primary MIME content type for an RDF format.
    /// </summary>
    public static string GetContentType(RdfFormat format)
        => RdfFormatNegotiator.GetContentType(format);

    #endregion

    #region Loading

    /// <summary>
    /// Load an RDF file into a QuadStore, detecting the format from the file extension.
    /// Uses batch writes for throughput.
    /// </summary>
    /// <param name="store">The store to load into.</param>
    /// <param name="filePath">Path to the RDF file.</param>
    /// <returns>The number of triples/quads loaded.</returns>
    public static async Task<long> LoadFileAsync(QuadStore store, string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        var extension = Path.GetExtension(filePath);
        var format = RdfFormatNegotiator.FromExtension(extension.AsSpan());

        if (format == RdfFormat.Unknown)
            throw new NotSupportedException($"Unknown RDF format for extension: {extension}");

        // Buffer file into memory to avoid thread-affinity issues:
        // LoadAsync holds a ReaderWriterLockSlim write lock during batch writes,
        // and FileStream.ReadAsync may resume on a different thread after await.
        await using var fileStream = File.OpenRead(filePath);
        using var memStream = new MemoryStream();
        await fileStream.CopyToAsync(memStream).ConfigureAwait(false);
        memStream.Position = 0;

        return await LoadAsync(store, memStream, format).ConfigureAwait(false);
    }

    /// <summary>
    /// Load RDF data from a stream into a QuadStore.
    /// Uses batch writes for throughput.
    /// </summary>
    /// <param name="store">The store to load into.</param>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="format">The RDF format of the stream data.</param>
    /// <param name="baseUri">Optional base URI for relative IRI resolution.</param>
    /// <returns>The number of triples/quads loaded.</returns>
    public static async Task<long> LoadAsync(QuadStore store, Stream stream, RdfFormat format, string? baseUri = null)
    {
        long count = 0;

        store.BeginBatch();
        try
        {
            switch (format)
            {
                case RdfFormat.Turtle:
                    using (var parser = new TurtleStreamParser(stream, baseUri: baseUri))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            store.AddCurrentBatched(subject, predicate, obj);
                            count++;
                        }).ConfigureAwait(false);
                    }
                    break;

                case RdfFormat.NTriples:
                    using (var parser = new NTriplesStreamParser(stream))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            store.AddCurrentBatched(subject, predicate, obj);
                            count++;
                        }).ConfigureAwait(false);
                    }
                    break;

                case RdfFormat.RdfXml:
                    using (var parser = new RdfXmlStreamParser(stream, baseUri: baseUri))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            store.AddCurrentBatched(subject, predicate, obj);
                            count++;
                        }).ConfigureAwait(false);
                    }
                    break;

                case RdfFormat.NQuads:
                    using (var parser = new NQuadsStreamParser(stream))
                    {
                        await parser.ParseAsync((subject, predicate, obj, graph) =>
                        {
                            if (graph.IsEmpty)
                                store.AddCurrentBatched(subject, predicate, obj);
                            else
                                store.AddCurrentBatched(subject, predicate, obj, graph);
                            count++;
                        }).ConfigureAwait(false);
                    }
                    break;

                case RdfFormat.TriG:
                    using (var parser = new TriGStreamParser(stream, baseUri))
                    {
                        await parser.ParseAsync((subject, predicate, obj, graph) =>
                        {
                            if (graph.IsEmpty)
                                store.AddCurrentBatched(subject, predicate, obj);
                            else
                                store.AddCurrentBatched(subject, predicate, obj, graph);
                            count++;
                        }).ConfigureAwait(false);
                    }
                    break;

                case RdfFormat.JsonLd:
                    using (var parser = new JsonLdStreamParser(stream, baseUri))
                    {
                        await parser.ParseAsync((subject, predicate, obj, graph) =>
                        {
                            if (graph.IsEmpty)
                                store.AddCurrentBatched(subject, predicate, obj);
                            else
                                store.AddCurrentBatched(subject, predicate, obj, graph);
                            count++;
                        }).ConfigureAwait(false);
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unsupported RDF format: {format}");
            }

            store.CommitBatch();
        }
        catch
        {
            store.RollbackBatch();
            throw;
        }

        return count;
    }

    #endregion

    #region Parsing

    /// <summary>
    /// Parse RDF data from a stream and invoke a callback for each triple.
    /// For quad formats (NQuads, TriG, JsonLd), the graph component is ignored.
    /// </summary>
    public static async Task ParseAsync(Stream stream, RdfFormat format, RdfTripleHandler handler,
        string? baseUri = null, CancellationToken ct = default)
    {
        switch (format)
        {
            case RdfFormat.Turtle:
                using (var parser = new TurtleStreamParser(stream, baseUri: baseUri))
                {
                    await parser.ParseAsync((s, p, o) => handler(s, p, o), ct).ConfigureAwait(false);
                }
                break;

            case RdfFormat.NTriples:
                using (var parser = new NTriplesStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o) => handler(s, p, o), ct).ConfigureAwait(false);
                }
                break;

            case RdfFormat.RdfXml:
                using (var parser = new RdfXmlStreamParser(stream, baseUri: baseUri))
                {
                    await parser.ParseAsync((s, p, o) => handler(s, p, o), ct).ConfigureAwait(false);
                }
                break;

            case RdfFormat.NQuads:
                using (var parser = new NQuadsStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o, _) => handler(s, p, o), ct).ConfigureAwait(false);
                }
                break;

            case RdfFormat.TriG:
                using (var parser = new TriGStreamParser(stream, baseUri))
                {
                    await parser.ParseAsync((s, p, o, _) => handler(s, p, o), ct).ConfigureAwait(false);
                }
                break;

            case RdfFormat.JsonLd:
                using (var parser = new JsonLdStreamParser(stream, baseUri))
                {
                    await parser.ParseAsync((s, p, o, _) => handler(s, p, o), ct).ConfigureAwait(false);
                }
                break;

            default:
                throw new NotSupportedException($"Unsupported RDF format: {format}");
        }
    }

    /// <summary>
    /// Parse RDF data from a stream and materialize all triples as a list.
    /// </summary>
    public static async Task<List<(string Subject, string Predicate, string Object)>> ParseTriplesAsync(
        Stream stream, RdfFormat format, string? baseUri = null, CancellationToken ct = default)
    {
        var triples = new List<(string, string, string)>();

        await ParseAsync(stream, format, (s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        }, baseUri, ct).ConfigureAwait(false);

        return triples;
    }

    #endregion

    #region Writing

    /// <summary>
    /// Write triples to a TextWriter in the specified format.
    /// </summary>
    public static void WriteTriples(TextWriter writer, RdfFormat format,
        IEnumerable<(string Subject, string Predicate, string Object)> triples)
    {
        switch (format)
        {
            case RdfFormat.NTriples:
                using (var w = new NTriplesStreamWriter(writer))
                {
                    foreach (var (s, p, o) in triples)
                        w.WriteTriple(s.AsSpan(), p.AsSpan(), o.AsSpan());
                }
                break;

            case RdfFormat.Turtle:
                using (var w = new TurtleStreamWriter(writer))
                {
                    foreach (var (s, p, o) in triples)
                        w.WriteTriple(s.AsSpan(), p.AsSpan(), o.AsSpan());
                }
                break;

            case RdfFormat.RdfXml:
                using (var w = new RdfXmlStreamWriter(writer))
                {
                    w.WriteStartDocument();
                    foreach (var (s, p, o) in triples)
                        w.WriteTriple(s.AsSpan(), p.AsSpan(), o.AsSpan());
                    w.WriteEndDocument();
                }
                break;

            default:
                throw new NotSupportedException($"WriteTriples does not support format: {format}. Use WriteQuads for quad formats.");
        }
    }

    /// <summary>
    /// Write quads to a TextWriter in the specified format.
    /// </summary>
    public static void WriteQuads(TextWriter writer, RdfFormat format,
        IEnumerable<(string Subject, string Predicate, string Object, string Graph)> quads)
    {
        switch (format)
        {
            case RdfFormat.NQuads:
                using (var w = new NQuadsStreamWriter(writer))
                {
                    foreach (var (s, p, o, g) in quads)
                        w.WriteQuad(s.AsSpan(), p.AsSpan(), o.AsSpan(), g.AsSpan());
                }
                break;

            case RdfFormat.TriG:
                using (var w = new TriGStreamWriter(writer))
                {
                    foreach (var (s, p, o, g) in quads)
                        w.WriteQuad(s, p, o, g);
                }
                break;

            case RdfFormat.JsonLd:
                using (var w = new JsonLdStreamWriter(writer))
                {
                    foreach (var (s, p, o, g) in quads)
                        w.WriteQuad(s, p, o, g);
                    w.Flush();
                }
                break;

            default:
                throw new NotSupportedException($"WriteQuads does not support format: {format}. Use WriteTriples for triple formats.");
        }
    }

    #endregion
}
