// LoadExecutor.cs
// Executes SPARQL LOAD operation with content negotiation
// Supports Turtle, N-Triples, and RDF/XML formats
// Uses BCL HttpClient (no external dependencies)

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Options for configuring LoadExecutor behavior and limits.
/// </summary>
public sealed class LoadExecutorOptions
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
public sealed class LoadExecutor : IDisposable
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
    /// Execute LOAD operation - fetch RDF data from URL and add to store.
    /// </summary>
    /// <param name="sourceUri">URL of RDF document to load.</param>
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        var graphStr = string.IsNullOrEmpty(destinationGraph) ? null : destinationGraph;

        try
        {
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

            int count = 0;

            store.BeginBatch();
            try
            {
                count = await ParseAndLoadAsync(stream, format, graphStr, store, _options.MaxTripleCount, ct)
                    .ConfigureAwait(false);
                store.CommitBatch();

                return new UpdateResult { Success = true, AffectedCount = count };
            }
            catch (TripleLimitExceededException ex)
            {
                store.RollbackBatch();
                return new UpdateResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
            catch (SizeLimitExceededException ex)
            {
                store.RollbackBatch();
                return new UpdateResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
            catch
            {
                store.RollbackBatch();
                throw;
            }
        }
        catch (Exception) when (silent)
        {
            // SILENT modifier - suppress errors
            return new UpdateResult { Success = true, AffectedCount = 0 };
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
    /// Determine RDF format from content type or URL extension.
    /// </summary>
    private static RdfFormat DetermineFormat(string? contentType, string uri)
    {
        // First try content type
        if (!string.IsNullOrEmpty(contentType))
        {
            var ct = contentType.ToLowerInvariant();

            if (ct.Contains("turtle") || ct == "text/turtle" || ct == "application/x-turtle")
                return RdfFormat.Turtle;

            if (ct.Contains("n-triples") || ct == "application/n-triples")
                return RdfFormat.NTriples;

            if (ct.Contains("rdf+xml") || ct == "application/rdf+xml")
                return RdfFormat.RdfXml;

            if (ct == "text/xml" || ct == "application/xml")
                return RdfFormat.RdfXml;
        }

        // Fall back to URL extension
        var lowerUri = uri.ToLowerInvariant();

        if (lowerUri.EndsWith(".ttl") || lowerUri.EndsWith(".turtle"))
            return RdfFormat.Turtle;

        if (lowerUri.EndsWith(".nt") || lowerUri.EndsWith(".ntriples"))
            return RdfFormat.NTriples;

        if (lowerUri.EndsWith(".rdf") || lowerUri.EndsWith(".xml") || lowerUri.EndsWith(".rdfxml"))
            return RdfFormat.RdfXml;

        // Default to Turtle as it's the most common
        return RdfFormat.Turtle;
    }

    /// <summary>
    /// Parse stream and load triples into store.
    /// </summary>
    private static async Task<int> ParseAndLoadAsync(
        Stream stream,
        RdfFormat format,
        string? graphStr,
        QuadStore store,
        int maxTripleCount,
        CancellationToken ct)
    {
        int count = 0;

        void AddTriple(ReadOnlySpan<char> s, ReadOnlySpan<char> p, ReadOnlySpan<char> o)
        {
            if (maxTripleCount > 0 && count >= maxTripleCount)
            {
                throw new TripleLimitExceededException(maxTripleCount);
            }

            if (graphStr == null)
                store.AddCurrentBatched(s, p, o);
            else
                store.AddCurrentBatched(s, p, o, graphStr.AsSpan());
            count++;
        }

        switch (format)
        {
            case RdfFormat.Turtle:
                using (var parser = new TurtleStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o) => AddTriple(s, p, o), ct);
                }
                break;

            case RdfFormat.NTriples:
                using (var parser = new NTriplesStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o) => AddTriple(s, p, o), ct);
                }
                break;

            case RdfFormat.RdfXml:
                using (var parser = new RdfXmlStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o) => AddTriple(s, p, o), ct);
                }
                break;

            default:
                throw new NotSupportedException($"Unsupported RDF format: {format}");
        }

        return count;
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
/// Supported RDF serialization formats.
/// </summary>
public enum RdfFormat
{
    Turtle,
    NTriples,
    RdfXml
}

/// <summary>
/// Exception thrown when the triple count limit is exceeded during loading.
/// </summary>
public sealed class TripleLimitExceededException : Exception
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
public sealed class SizeLimitExceededException : IOException
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
