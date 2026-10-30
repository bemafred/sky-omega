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
    /// Determine the RDF format from a file extension (<c>.nt</c> or <c>nt</c>), which is also how a command-line
    /// option names a format.
    /// </summary>
    public static RdfFormat DetermineFormatFromExtension(ReadOnlySpan<char> extension)
        => RdfFormatNegotiator.FromExtension(extension);

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

        // The stream is one document: its blank nodes are its own
        var blankNodes = new BlankNodeScope();

        void OnQuad(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj, ReadOnlySpan<char> graph)
        {
            if (limit.HasValue && totalCount >= limit.Value)
            {
                cts!.Cancel();
                return;
            }
            buffer.Add((graph.IsEmpty ? string.Empty : blankNodes.Graph(graph).ToString(), blankNodes.Subject(subject).ToString(),
                predicate.ToString(), blankNodes.Object(obj).ToString()));
            totalCount++;
            if (buffer.Count >= chunkSize)
                FlushBuffer();
        }

        try
        {
            await ParseQuadsAsync(stream, format, OnQuad, baseUri, ct).ConfigureAwait(false);
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
    /// GZip is BCL (System.IO.Compression). BZip2 is BCL-only via the substrate
    /// <see cref="SkyOmega.Mercury.Compression.BZip2DecompressorStream"/> (ADR-036).
    /// </summary>
    internal static Stream WrapWithDecompression(Stream stream, CompressionType compression)
    {
        return compression switch
        {
            CompressionType.GZip => new GZipStream(stream, CompressionMode.Decompress),
            CompressionType.BZip2 => new SkyOmega.Mercury.Compression.BZip2DecompressorStream(stream, leaveOpen: false),
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
        // Parsed first, then written in one batch with no await inside it: the batch's write lock belongs to the thread
        // that took it, and an await may resume on another. The stream is one document: its blank nodes are its own.
        var blankNodes = new BlankNodeScope();
        var quads = new List<(string Subject, string Predicate, string Object, string Graph)>();
        await ParseQuadsAsync(stream, format, (subject, predicate, obj, graph) =>
            quads.Add((blankNodes.Subject(subject).ToString(), predicate.ToString(), blankNodes.Object(obj).ToString(),
                graph.IsEmpty ? string.Empty : blankNodes.Graph(graph).ToString())), baseUri).ConfigureAwait(false);

        store.BeginBatch();
        try
        {
            foreach (var (s, p, o, g) in quads)
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

        return quads.Count;
    }

    #endregion

    #region Parsing

    /// <summary>
    /// Parse RDF data from a stream and invoke a callback for each triple.
    /// For quad formats (NQuads, TriG, JsonLd), the graph component is ignored. Labels are as the document writes them;
    /// loading into a store scopes them (<see cref="BlankNodeScope"/>).
    /// </summary>
    public static Task ParseAsync(Stream stream, RdfFormat format, RdfTripleHandler handler,
        string? baseUri = null, CancellationToken ct = default)
        => ParseQuadsAsync(stream, format, (s, p, o, _) => handler(s, p, o), baseUri, ct);

    /// <summary>
    /// The one parse of a document in any supported format, as quads: a triple format reports the default graph
    /// (an empty graph). Every load and parse in <see cref="RdfEngine"/> and SPARQL LOAD reads a document through it.
    /// </summary>
    public static async Task ParseQuadsAsync(Stream stream, RdfFormat format, RdfQuadHandler handler,
        string? baseUri = null, CancellationToken ct = default)
    {
        switch (format)
        {
            case RdfFormat.Turtle:
                using (var parser = new TurtleStreamParser(stream, baseUri: baseUri))
                    await parser.ParseAsync((s, p, o) => handler(s, p, o, ReadOnlySpan<char>.Empty), ct).ConfigureAwait(false);
                break;

            case RdfFormat.NTriples:
                using (var parser = new NTriplesStreamParser(stream))
                    await parser.ParseAsync((s, p, o) => handler(s, p, o, ReadOnlySpan<char>.Empty), ct).ConfigureAwait(false);
                break;

            case RdfFormat.RdfXml:
                using (var parser = new RdfXmlStreamParser(stream, baseUri: baseUri))
                    await parser.ParseAsync((s, p, o) => handler(s, p, o, ReadOnlySpan<char>.Empty), ct).ConfigureAwait(false);
                break;

            case RdfFormat.NQuads:
                using (var parser = new NQuadsStreamParser(stream))
                    await parser.ParseAsync((s, p, o, g) => handler(s, p, o, g), ct).ConfigureAwait(false);
                break;

            case RdfFormat.TriG:
                using (var parser = new TriGStreamParser(stream, baseUri))
                    await parser.ParseAsync((s, p, o, g) => handler(s, p, o, g), ct).ConfigureAwait(false);
                break;

            case RdfFormat.JsonLd:
                using (var parser = new JsonLdStreamParser(stream, baseUri))
                    await parser.ParseAsync((s, p, o, g) => handler(s, p, o, g), ct).ConfigureAwait(false);
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
        if (RdfDocumentWriter.HoldsNamedGraphs(format))
            throw new NotSupportedException($"WriteTriples does not support format: {format}. Use WriteQuads for quad formats.");

        using var document = new RdfDocumentWriter(writer, format);
        foreach (var (s, p, o) in triples)
            document.Write(s.AsSpan(), p.AsSpan(), o.AsSpan(), ReadOnlySpan<char>.Empty);
    }

    /// <summary>
    /// Write quads to a TextWriter in the specified format.
    /// </summary>
    public static void WriteQuads(TextWriter writer, RdfFormat format,
        IEnumerable<(string Subject, string Predicate, string Object, string Graph)> quads)
    {
        if (!RdfDocumentWriter.HoldsNamedGraphs(format))
            throw new NotSupportedException($"WriteQuads does not support format: {format}. Use WriteTriples for triple formats.");

        using var document = new RdfDocumentWriter(writer, format);
        foreach (var (s, p, o, g) in quads)
            document.Write(s.AsSpan(), p.AsSpan(), o.AsSpan(), g.AsSpan());
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

        // Every output format through its writer, which re-escapes the parsers' decoded literals. Until 2026-10-30 only
        // N-Triples was written: the other formats counted the statements and left an empty file. Statements keep their
        // graph; a named graph written to a format without named graphs is an error (RdfDocumentWriter).
        long count = 0;
        var sw = Stopwatch.StartNew();

        using var cts = limit.HasValue ? new CancellationTokenSource() : null;
        var ct = cts?.Token ?? CancellationToken.None;

        using (var document = new RdfDocumentWriter(streamWriter, outputFormat))
        {
            try
            {
                await ParseQuadsAsync(inputStream, inputFormat, (s, p, o, g) =>
                {
                    if (limit.HasValue && count >= limit.Value)
                    {
                        cts!.Cancel();
                        return;
                    }
                    count++;
                    document.Write(s, p, o, g);

                    if (count % 1_000_000 == 0)
                        onProgress?.Invoke(new LoadProgress { TriplesLoaded = count, Elapsed = sw.Elapsed });
                }, ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.HasValue && count >= limit.Value)
            {
                // Expected: parser cancelled once the limit was reached.
            }
        }

        if (inputStream != inputFileStream)
            await inputStream.DisposeAsync().ConfigureAwait(false);

        onProgress?.Invoke(new LoadProgress { TriplesLoaded = count, Elapsed = sw.Elapsed });

        return count;
    }

    #endregion
}
