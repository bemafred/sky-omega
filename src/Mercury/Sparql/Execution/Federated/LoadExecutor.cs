// LoadExecutor.cs
// Executes SPARQL LOAD operation with content negotiation
// Supports Turtle, N-Triples, and RDF/XML formats
// Uses BCL HttpClient (no external dependencies)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution.Federated;

/// <summary>
/// Options for configuring LoadExecutor behavior and limits.
/// </summary>
internal sealed class LoadExecutorOptions
{
    /// <summary>
    /// Default maximum download size: 100MB.
    /// </summary>
    public const long DefaultMaxDownloadSize = 100L << 20; // 100MB

    /// <summary>
    /// Default maximum triple count per load: 10 million.
    /// </summary>
    public const int DefaultMaxTripleCount = 10_000_000;

    /// <summary>
    /// Maximum size in bytes to download from a URL. Default: 100MB.
    /// Set to 0 for unlimited (not recommended).
    /// </summary>
    public long MaxDownloadSize { get; init; } = DefaultMaxDownloadSize;

    /// <summary>
    /// Maximum number of triples to load from a single source. Default: 10 million.
    /// Set to 0 for unlimited.
    /// </summary>
    public int MaxTripleCount { get; init; } = DefaultMaxTripleCount;

    /// <summary>
    /// Timeout for the entire load operation. Default: 5 minutes.
    /// </summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to enforce Content-Length header check before downloading.
    /// If true and Content-Length exceeds MaxDownloadSize, the request is rejected early.
    /// Default: true.
    /// </summary>
    public bool EnforceContentLength { get; init; } = true;

    /// <summary>
    /// Default options with reasonable limits.
    /// </summary>
    public static LoadExecutorOptions Default { get; } = new();

    /// <summary>
    /// Options with no limits (for testing or trusted sources only).
    /// </summary>
    public static LoadExecutorOptions Unlimited { get; } = new()
    {
        MaxDownloadSize = 0,
        MaxTripleCount = 0,
        LoadTimeout = Timeout.InfiniteTimeSpan,
        EnforceContentLength = false
    };
}

/// <summary>
/// Executes SPARQL LOAD operation by fetching RDF data from a URL
/// and loading it into a QuadStore.
///
/// Supports content negotiation for:
/// - Turtle (text/turtle, application/x-turtle)
/// - N-Triples (application/n-triples, text/plain with .nt extension)
/// - RDF/XML (application/rdf+xml, text/xml, application/xml)
/// </summary>
internal sealed class LoadExecutor : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly LoadExecutorOptions _options;
    private bool _disposed;

    /// <summary>
    /// Creates a new LoadExecutor with a default HttpClient and default options.
    /// </summary>
    public LoadExecutor() : this(LoadExecutorOptions.Default)
    {
    }

    /// <summary>
    /// Creates a new LoadExecutor with a default HttpClient and specified options.
    /// </summary>
    /// <param name="options">Options for configuring limits and behavior.</param>
    public LoadExecutor(LoadExecutorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "text/turtle, application/n-triples, application/rdf+xml;q=0.9, */*;q=0.1");

        if (options.LoadTimeout != Timeout.InfiniteTimeSpan)
            _httpClient.Timeout = options.LoadTimeout;

        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a new LoadExecutor with the provided HttpClient and default options.
    /// </summary>
    /// <param name="httpClient">HttpClient to use for requests (not disposed by this class).</param>
    public LoadExecutor(HttpClient httpClient) : this(httpClient, LoadExecutorOptions.Default)
    {
    }

    /// <summary>
    /// Creates a new LoadExecutor with the provided HttpClient and options.
    /// </summary>
    /// <param name="httpClient">HttpClient to use for requests (not disposed by this class).</param>
    /// <param name="options">Options for configuring limits and behavior.</param>
    public LoadExecutor(HttpClient httpClient, LoadExecutorOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttpClient = false;
    }

    /// <summary>
    /// Execute LOAD operation - fetch RDF data from URL or local file and add to store.
    /// </summary>
    /// <param name="sourceUri">URL or file:// URI of RDF document to load.</param>
    /// <param name="destinationGraph">Target named graph (null or empty for default graph).</param>
    /// <param name="silent">If true, suppress errors and return success with 0 affected.</param>
    /// <param name="store">QuadStore to load data into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UpdateResult indicating success and number of triples loaded.</returns>
    public async ValueTask<UpdateResult> ExecuteAsync(
        string sourceUri,
        string? destinationGraph,
        bool silent,
        QuadStore store,
        CancellationToken ct = default)
    {
        var result = await LoadAsync(sourceUri, destinationGraph, store, ct).ConfigureAwait(false);

        // SILENT suppresses every failure: the ones a load reports (file not found, size or
        // triple limit) as well as the ones it throws. The load rolls its own batch back, so a
        // suppressed failure leaves the store unchanged.
        return silent && !result.Success
            ? new UpdateResult { Success = true, AffectedCount = 0 }
            : result;
    }

    private async ValueTask<UpdateResult> LoadAsync(
        string sourceUri,
        string? destinationGraph,
        QuadStore store,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var graphStr = string.IsNullOrEmpty(destinationGraph) ? null : destinationGraph;

        try
        {
            // Handle file:// URIs locally
            if (sourceUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return await LoadFromFileAsync(sourceUri, graphStr, store, ct).ConfigureAwait(false);
            }

            using var response = await _httpClient.GetAsync(sourceUri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            // Check Content-Length if enforcement is enabled
            if (_options.EnforceContentLength && _options.MaxDownloadSize > 0)
            {
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > _options.MaxDownloadSize)
                {
                    return new UpdateResult
                    {
                        Success = false,
                        ErrorMessage = $"Content size ({contentLength.Value:N0} bytes) exceeds maximum allowed ({_options.MaxDownloadSize:N0} bytes)"
                    };
                }
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;

            // Wrap stream with size-limited stream if limit is set
            Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (_options.MaxDownloadSize > 0)
            {
                stream = new SizeLimitedStream(stream, _options.MaxDownloadSize);
            }

            // Determine format from content type or URL extension
            var format = DetermineFormat(contentType, sourceUri);

            // Parsed first, then written in one batch with no await inside it (the batch's lock is thread-affine)
            try
            {
                var quads = await ParseDocumentAsync(stream, format, graphStr, _options.MaxTripleCount, ct)
                    .ConfigureAwait(false);
                return WriteDocument(store, quads);
            }
            catch (TripleLimitExceededException ex)
            {
                return new UpdateResult { Success = false, ErrorMessage = ex.Message };
            }
            catch (SizeLimitExceededException ex)
            {
                return new UpdateResult { Success = false, ErrorMessage = ex.Message };
            }
        }
        catch (HttpRequestException ex)
        {
            return new UpdateResult
            {
                Success = false,
                ErrorMessage = $"HTTP error loading {sourceUri}: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new UpdateResult
            {
                Success = false,
                ErrorMessage = $"Error loading {sourceUri}: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Load RDF data from a file:// URI.
    /// Runs on a dedicated thread to maintain lock affinity — BeginBatch/CommitBatch
    /// use ReaderWriterLockSlim which is thread-affined, so the entire batch must
    /// execute on one thread without async continuations switching threads.
    /// </summary>
    private ValueTask<UpdateResult> LoadFromFileAsync(
        string sourceUri,
        string? graphStr,
        QuadStore store,
        CancellationToken ct)
    {
        var filePath = new Uri(sourceUri).LocalPath;

        if (!File.Exists(filePath))
        {
            return new ValueTask<UpdateResult>(new UpdateResult
            {
                Success = false,
                ErrorMessage = $"File not found: {filePath}"
            });
        }

        // Check file size against limit
        if (_options.MaxDownloadSize > 0)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > _options.MaxDownloadSize)
            {
                return new ValueTask<UpdateResult>(new UpdateResult
                {
                    Success = false,
                    ErrorMessage = $"File size ({fileInfo.Length:N0} bytes) exceeds maximum allowed ({_options.MaxDownloadSize:N0} bytes)"
                });
            }
        }

        var format = DetermineFormat(null, sourceUri);
        var maxTripleCount = _options.MaxTripleCount;

        // Parsed first, then written in one batch with no await inside it (the batch's lock is thread-affine)
        var task = Task.Run(async () =>
        {
            await using var stream = File.OpenRead(filePath);
            try
            {
                var quads = await ParseDocumentAsync(stream, format, graphStr, maxTripleCount, ct).ConfigureAwait(false);
                return WriteDocument(store, quads);
            }
            catch (TripleLimitExceededException ex)
            {
                return new UpdateResult { Success = false, ErrorMessage = ex.Message };
            }
        }, ct);

        return new ValueTask<UpdateResult>(task);
    }

    /// <summary>
    /// The document's format, by the shared negotiation (<see cref="RdfFormatNegotiator.Negotiate"/>: the content type,
    /// then the extension); Turtle when neither says.
    /// </summary>
    private static RdfFormat DetermineFormat(string? contentType, string uri)
    {
        // text/plain is what servers send for any file they do not know; it says nothing about the format here
        if (contentType != null && contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
            contentType = null;
        var format = RdfFormatNegotiator.Negotiate(contentType, uri);
        return format == RdfFormat.Unknown ? RdfFormat.Turtle : format;
    }

    /// <summary>
    /// The document's quads as they go into the store: into <paramref name="graphStr"/> (LOAD … INTO GRAPH), or where the
    /// document puts them. Read with <see cref="RdfEngine.ParseQuadsAsync"/>, the one parse of every format; the
    /// document's blank nodes are its own (<see cref="BlankNodeScope"/>). Stops at <paramref name="maxTripleCount"/>.
    /// </summary>
    private static async Task<List<(string S, string P, string O, string G)>> ParseDocumentAsync(
        Stream stream,
        RdfFormat format,
        string? graphStr,
        int maxTripleCount,
        CancellationToken ct)
    {
        var quads = new List<(string S, string P, string O, string G)>();
        var blankNodes = new BlankNodeScope();

        await RdfEngine.ParseQuadsAsync(stream, format, (s, p, o, g) =>
        {
            if (maxTripleCount > 0 && quads.Count >= maxTripleCount)
                throw new TripleLimitExceededException(maxTripleCount);

            var graph = graphStr ?? (g.IsEmpty ? string.Empty : blankNodes.Graph(g).ToString());
            quads.Add((blankNodes.Subject(s).ToString(), p.ToString(), blankNodes.Object(o).ToString(), graph));
        }, ct: ct).ConfigureAwait(false);

        return quads;
    }

    /// <summary>Writes a parsed document in one batch, all or nothing.</summary>
    private static UpdateResult WriteDocument(QuadStore store, List<(string S, string P, string O, string G)> quads)
    {
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
            return new UpdateResult { Success = true, AffectedCount = quads.Count };
        }
        catch
        {
            store.RollbackBatch();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

/// <summary>
/// Exception thrown when the triple count limit is exceeded during loading.
/// </summary>
internal sealed class TripleLimitExceededException : Exception
{
    public int Limit { get; }

    public TripleLimitExceededException(int limit)
        : base($"Triple count limit ({limit:N0}) exceeded during load operation")
    {
        Limit = limit;
    }
}

/// <summary>
/// Exception thrown when download size limit is exceeded.
/// </summary>
internal sealed class SizeLimitExceededException : IOException
{
    public long Limit { get; }
    public long BytesRead { get; }

    public SizeLimitExceededException(long limit, long bytesRead)
        : base($"Download size limit ({limit:N0} bytes) exceeded at {bytesRead:N0} bytes")
    {
        Limit = limit;
        BytesRead = bytesRead;
    }
}

/// <summary>
/// A stream wrapper that throws when a size limit is exceeded.
/// </summary>
internal sealed class SizeLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _totalRead;

    public SizeLimitedStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _inner.Read(buffer, offset, count);
        _totalRead += bytesRead;

        if (_totalRead > _limit)
            throw new SizeLimitExceededException(_limit, _totalRead);

        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
        _totalRead += bytesRead;

        if (_totalRead > _limit)
            throw new SizeLimitExceededException(_limit, _totalRead);

        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        _totalRead += bytesRead;

        if (_totalRead > _limit)
            throw new SizeLimitExceededException(_limit, _totalRead);

        return bytesRead;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
