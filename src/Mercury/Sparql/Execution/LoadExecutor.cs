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
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

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
    private bool _disposed;

    /// <summary>
    /// Creates a new LoadExecutor with a default HttpClient.
    /// </summary>
    public LoadExecutor()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "text/turtle, application/n-triples, application/rdf+xml;q=0.9, */*;q=0.1");
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a new LoadExecutor with the provided HttpClient.
    /// </summary>
    /// <param name="httpClient">HttpClient to use for requests (not disposed by this class).</param>
    public LoadExecutor(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            // Determine format from content type or URL extension
            var format = DetermineFormat(contentType, sourceUri);

            int count = 0;

            store.BeginBatch();
            try
            {
                count = await ParseAndLoadAsync(stream, format, graphStr, store, ct).ConfigureAwait(false);
                store.CommitBatch();

                return new UpdateResult { Success = true, AffectedCount = count };
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
        CancellationToken ct)
    {
        int count = 0;

        switch (format)
        {
            case RdfFormat.Turtle:
                using (var parser = new TurtleStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o) =>
                    {
                        if (graphStr == null)
                            store.AddCurrentBatched(s, p, o);
                        else
                            store.AddCurrentBatched(s, p, o, graphStr.AsSpan());
                        count++;
                    }, ct);
                }
                break;

            case RdfFormat.NTriples:
                using (var parser = new NTriplesStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o) =>
                    {
                        if (graphStr == null)
                            store.AddCurrentBatched(s, p, o);
                        else
                            store.AddCurrentBatched(s, p, o, graphStr.AsSpan());
                        count++;
                    }, ct);
                }
                break;

            case RdfFormat.RdfXml:
                using (var parser = new RdfXmlStreamParser(stream))
                {
                    await parser.ParseAsync((s, p, o) =>
                    {
                        if (graphStr == null)
                            store.AddCurrentBatched(s, p, o);
                        else
                            store.AddCurrentBatched(s, p, o, graphStr.AsSpan());
                        count++;
                    }, ct);
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
