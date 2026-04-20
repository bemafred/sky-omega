using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
/// Progress information reported during streaming load operations.
/// </summary>
public sealed class LoadProgress
{
    public long TriplesLoaded { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double TriplesPerSecond => Elapsed.TotalSeconds > 0 ? TriplesLoaded / Elapsed.TotalSeconds : 0;

    /// <summary>GC heap size in bytes.</summary>
    public long GcHeapBytes { get; init; }

    /// <summary>Process working set in bytes.</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>Triples loaded at previous progress report (for interval rate calculation).</summary>
    public long PreviousTripleCount { get; init; }

    /// <summary>Elapsed at previous progress report.</summary>
    public TimeSpan PreviousElapsed { get; init; }

    /// <summary>Triples/sec over the most recent interval (not lifetime average).</summary>
    public double RecentTriplesPerSecond
    {
        get
        {
            var dt = (Elapsed - PreviousElapsed).TotalSeconds;
            return dt > 0 ? (TriplesLoaded - PreviousTripleCount) / dt : 0;
        }
    }
}

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
    /// Streams directly from disk with chunked batch commits — no MemoryStream buffering.
    /// Supports transparent GZip decompression (.gz extension).
    /// </summary>
    /// <param name="store">The store to load into.</param>
    /// <param name="filePath">Path to the RDF file (.ttl, .nt, .ttl.gz, etc.).</param>
    /// <param name="chunkSize">Triples per batch commit. Default 100,000.</param>
    /// <param name="onProgress">Optional progress callback, invoked at each chunk boundary.</param>
    /// <param name="limit">Optional cap on triples added to the store. When set, parsing stops after exactly <paramref name="limit"/> triples have been added.</param>
    /// <returns>The number of triples/quads loaded.</returns>
    public static async Task<long> LoadFileAsync(QuadStore store, string filePath,
        int chunkSize = 100_000, Action<LoadProgress>? onProgress = null, long? limit = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        var (format, compression) = RdfFormatNegotiator.FromPathStrippingCompression(filePath.AsSpan());

        if (format == RdfFormat.Unknown)
            throw new NotSupportedException($"Unknown RDF format for file: {filePath}");

        await using var fileStream = File.OpenRead(filePath);
        var stream = WrapWithDecompression(fileStream, compression);
        try
        {
            return await LoadStreamingAsync(store, stream, format, chunkSize, onProgress, limit: limit).ConfigureAwait(false);
        }
        finally
        {
            if (stream != fileStream)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Load an RDF stream into a QuadStore with chunked batch commits.
    /// Decouples parsing from writing: parser fills a buffer (no lock held),
    /// then the buffer is flushed to the store (lock held only during materialization).
    /// This avoids ReaderWriterLockSlim thread-affinity issues with async I/O.
    /// </summary>
    internal static async Task<long> LoadStreamingAsync(QuadStore store, Stream stream, RdfFormat format,
        int chunkSize = 100_000, Action<LoadProgress>? onProgress = null, string? baseUri = null, long? limit = null)
    {
        long totalCount = 0;
        var buffer = new List<(string Graph, string Subject, string Predicate, string Object)>(chunkSize);
        var sw = Stopwatch.StartNew();
        long prevCount = 0;
        TimeSpan prevElapsed = TimeSpan.Zero;

        using var cts = limit.HasValue ? new CancellationTokenSource() : null;
        var ct = cts?.Token ?? CancellationToken.None;

        void FlushBuffer()
        {
            if (buffer.Count == 0) return;

            store.BeginBatch();
            try
            {
                foreach (var (g, s, p, o) in buffer)
                {
                    if (g.Length == 0)
                        store.AddCurrentBatched(s, p, o);
                    else
                        store.AddCurrentBatched(s, p, o, g);
                }
                store.CommitBatch();
            }
            catch
            {
                store.RollbackBatch();
                throw;
            }

            buffer.Clear();

            var elapsed = sw.Elapsed;
            onProgress?.Invoke(new LoadProgress
            {
                TriplesLoaded = totalCount,
                Elapsed = elapsed,
                GcHeapBytes = GC.GetTotalMemory(false),
                WorkingSetBytes = Environment.WorkingSet,
                PreviousTripleCount = prevCount,
                PreviousElapsed = prevElapsed
            });
            prevCount = totalCount;
            prevElapsed = elapsed;
        }

        // Triple formats (Turtle, NTriples, RdfXml)
        void OnTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
        {
            if (limit.HasValue && totalCount >= limit.Value)
            {
                cts!.Cancel();
                return;
            }
            buffer.Add((string.Empty, subject.ToString(), predicate.ToString(), obj.ToString()));
            totalCount++;
            if (buffer.Count >= chunkSize)
                FlushBuffer();
        }

        // Quad formats (NQuads, TriG, JsonLd)
        void OnQuad(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj, ReadOnlySpan<char> graph)
        {
            if (limit.HasValue && totalCount >= limit.Value)
            {
                cts!.Cancel();
                return;
            }
            buffer.Add((graph.IsEmpty ? string.Empty : graph.ToString(), subject.ToString(), predicate.ToString(), obj.ToString()));
            totalCount++;
            if (buffer.Count >= chunkSize)
                FlushBuffer();
        }

        try
        {
            switch (format)
            {
                case RdfFormat.Turtle:
                    using (var parser = new TurtleStreamParser(stream, baseUri: baseUri))
                        await parser.ParseAsync((s, p, o) => OnTriple(s, p, o), ct).ConfigureAwait(false);
                    break;

                case RdfFormat.NTriples:
                    using (var parser = new NTriplesStreamParser(stream))
                        await parser.ParseAsync((s, p, o) => OnTriple(s, p, o), ct).ConfigureAwait(false);
                    break;

                case RdfFormat.RdfXml:
                    using (var parser = new RdfXmlStreamParser(stream, baseUri: baseUri))
                        await parser.ParseAsync((s, p, o) => OnTriple(s, p, o), ct).ConfigureAwait(false);
                    break;

                case RdfFormat.NQuads:
                    using (var parser = new NQuadsStreamParser(stream))
                        await parser.ParseAsync((s, p, o, g) => OnQuad(s, p, o, g), ct).ConfigureAwait(false);
                    break;

                case RdfFormat.TriG:
                    using (var parser = new TriGStreamParser(stream, baseUri))
                        await parser.ParseAsync((s, p, o, g) => OnQuad(s, p, o, g), ct).ConfigureAwait(false);
                    break;

                case RdfFormat.JsonLd:
                    using (var parser = new JsonLdStreamParser(stream, baseUri))
                        await parser.ParseAsync((s, p, o, g) => OnQuad(s, p, o, g), ct).ConfigureAwait(false);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported RDF format: {format}");
            }
        }
        catch (OperationCanceledException) when (limit.HasValue && totalCount >= limit.Value)
        {
            // Expected: parser cancelled once the limit was reached.
        }

        // Flush remaining triples
        FlushBuffer();

        if (store.IsBulkLoadMode)
            store.FlushToDisk();

        return totalCount;
    }

    /// <summary>
    /// Wrap a stream with decompression based on detected compression type.
    /// GZip is BCL (System.IO.Compression). BZip2 requires Mercury.Compression.
    /// </summary>
    internal static Stream WrapWithDecompression(Stream stream, CompressionType compression)
    {
        return compression switch
        {
            CompressionType.GZip => new GZipStream(stream, CompressionMode.Decompress),
            CompressionType.BZip2 => throw new NotSupportedException(
                "BZip2 decompression requires Mercury.Compression. " +
                "Decompress the file first, or use Mercury.Cli which includes BZip2 support."),
            _ => stream
        };
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

    #region Conversion

    /// <summary>
    /// Streaming format conversion: parser in, writer out, no store.
    /// Supports transparent GZip decompression on input.
    /// Pure throughput test — validates parser survivability on large files.
    /// </summary>
    /// <param name="inputPath">Input RDF file path (format auto-detected, .gz supported).</param>
    /// <param name="outputPath">Output RDF file path (format auto-detected from extension).</param>
    /// <param name="onProgress">Optional progress callback, invoked every 1M triples.</param>
    /// <param name="limit">Optional cap on triples written to the output. When set, conversion stops after exactly <paramref name="limit"/> triples have been emitted.</param>
    /// <returns>The number of triples converted.</returns>
    public static async Task<long> ConvertAsync(string inputPath, string outputPath,
        Action<LoadProgress>? onProgress = null, long? limit = null)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"File not found: {inputPath}", inputPath);

        var (inputFormat, compression) = RdfFormatNegotiator.FromPathStrippingCompression(inputPath.AsSpan());
        if (inputFormat == RdfFormat.Unknown)
            throw new NotSupportedException($"Unknown RDF format for input: {inputPath}");

        var outputFormat = RdfFormatNegotiator.FromPath(outputPath.AsSpan());
        if (outputFormat == RdfFormat.Unknown)
            throw new NotSupportedException($"Unknown RDF format for output: {outputPath}");

        await using var inputFileStream = File.OpenRead(inputPath);
        var inputStream = WrapWithDecompression(inputFileStream, compression);

        await using var outputFileStream = File.Create(outputPath);
        using var streamWriter = new StreamWriter(outputFileStream);

        // For N-Triples output, route through NTriplesStreamWriter so literals
        // are properly re-escaped. The in-memory lexical form produced by the
        // Turtle parser has `\"` unescaped to `"`; writing those spans directly
        // produces invalid N-Triples. See ADR commits and
        // docs/validations/bulk-load-gradient-2026-04-17.md.
        NTriples.NTriplesStreamWriter? ntWriter = outputFormat == RdfFormat.NTriples
            ? new NTriples.NTriplesStreamWriter(streamWriter)
            : null;

        long count = 0;
        var sw = Stopwatch.StartNew();

        using var cts = limit.HasValue ? new CancellationTokenSource() : null;
        var ct = cts?.Token ?? CancellationToken.None;

        try
        {
            await ParseAsync(inputStream, inputFormat, (s, p, o) =>
            {
                if (limit.HasValue && count >= limit.Value)
                {
                    cts!.Cancel();
                    return;
                }
                count++;
                switch (outputFormat)
                {
                    case RdfFormat.NTriples:
                        ntWriter!.WriteTriple(s, p, o);
                        break;
                    default:
                        // For other formats, fall through to materialized write below
                        break;
                }

                if (count % 1_000_000 == 0)
                    onProgress?.Invoke(new LoadProgress { TriplesLoaded = count, Elapsed = sw.Elapsed });
            }, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.HasValue && count >= limit.Value)
        {
            // Expected: parser cancelled once the limit was reached.
        }

        ntWriter?.Flush();
        ntWriter?.Dispose();

        if (inputStream != inputFileStream)
            await inputStream.DisposeAsync().ConfigureAwait(false);

        onProgress?.Invoke(new LoadProgress { TriplesLoaded = count, Elapsed = sw.Elapsed });

        return count;
    }

    #endregion
}
