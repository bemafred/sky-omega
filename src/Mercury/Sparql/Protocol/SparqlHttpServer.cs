// SparqlHttpServer.cs
// SPARQL Protocol HTTP server implementation
// Based on W3C SPARQL 1.1 Protocol
// https://www.w3.org/TR/sparql11-protocol/
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Results;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Protocol;

/// <summary>
/// SPARQL Protocol HTTP server using BCL HttpListener.
/// Implements W3C SPARQL 1.1 Protocol for query and update operations.
/// </summary>
/// <remarks>
/// Endpoints:
/// - GET/POST /sparql?query=... - Execute SPARQL query
/// - POST /sparql/update - Execute SPARQL Update
/// - GET /sparql - Service description (if no query parameter)
///
/// Content negotiation:
/// - Accept header determines result format (JSON, XML, CSV, TSV)
/// - Default format is JSON (application/sparql-results+json)
/// </remarks>
public sealed class SparqlHttpServer : IDisposable, IAsyncDisposable
{
    private readonly HttpListenerLoop _loop;
    private readonly Func<QuadStore> _storeFactory;
    private readonly SparqlHttpServerOptions _options;

    /// <summary>
    /// Creates a new SPARQL HTTP server with a store factory.
    /// Each request resolves the store via the factory, enabling seamless store switching
    /// (e.g., after pruning) without restarting the server.
    /// </summary>
    /// <param name="storeFactory">Factory function that returns the current QuadStore.</param>
    /// <param name="baseUri">Base URI to listen on (e.g., "http://localhost:8080/").</param>
    /// <param name="options">Server configuration options.</param>
    public SparqlHttpServer(Func<QuadStore> storeFactory, string baseUri, SparqlHttpServerOptions? options = null)
        : this(storeFactory, [baseUri], options)
    {
    }

    /// <summary>
    /// Creates a new SPARQL HTTP server with a store factory, on several addresses: one listener and one port, with
    /// every address answered (ADR-065 D4).
    /// </summary>
    /// <param name="storeFactory">Factory function that returns the current QuadStore.</param>
    /// <param name="baseUris">Base URIs to listen on, at least one (e.g., "http://localhost:3032/",
    /// "http://100.67.254.98:3032/"). A URI given twice is listened on once.</param>
    /// <param name="options">Server configuration options.</param>
    public SparqlHttpServer(Func<QuadStore> storeFactory, IEnumerable<string> baseUris, SparqlHttpServerOptions? options = null)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _loop = new HttpListenerLoop(baseUris, RespondAsync);
        _options = options ?? new SparqlHttpServerOptions();
    }

    /// <summary>
    /// Creates a new SPARQL HTTP server with a fixed store.
    /// </summary>
    /// <param name="store">The QuadStore to query and update.</param>
    /// <param name="baseUri">Base URI to listen on (e.g., "http://localhost:8080/").</param>
    /// <param name="options">Server configuration options.</param>
    public SparqlHttpServer(QuadStore store, string baseUri, SparqlHttpServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _storeFactory = () => store;
        _loop = new HttpListenerLoop([baseUri], RespondAsync);
        _options = options ?? new SparqlHttpServerOptions();
    }

    /// <summary>
    /// Start the HTTP server and begin accepting requests.
    /// </summary>
    public void Start() => _loop.Start();

    /// <summary>
    /// Stop the HTTP server gracefully: it stops taking requests, and returns once the responses in progress are
    /// written, so the store can be closed after it (ADR-065 D3). A request that comes meanwhile is answered 503.
    /// </summary>
    public Task StopAsync() => _loop.StopAsync();

    /// <summary>
    /// Whether the server is currently running.
    /// </summary>
    public bool IsListening => _loop.IsListening;

    /// <summary>
    /// The base URIs the server listens on, each with its trailing slash.
    /// </summary>
    public IReadOnlyList<string> BaseUris => _loop.BaseUris;

    /// <summary>
    /// One request: the query endpoint, the update endpoint, or 404. It returns the response's body, which the loop
    /// writes; the loop answers an exception with 500.
    /// </summary>
    private async Task<byte[]> RespondAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        // Add CORS headers if enabled
        if (_options.EnableCors)
        {
            response.Headers.Add("Access-Control-Allow-Origin", _options.CorsOrigin);
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");
        }

        // Handle preflight
        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            return [];
        }

        var path = request.Url?.AbsolutePath ?? "/";

        if (path.EndsWith("/sparql/update", StringComparison.OrdinalIgnoreCase))
            return await HandleUpdateRequestAsync(request, response, ct).ConfigureAwait(false);
        if (path.EndsWith("/sparql", StringComparison.OrdinalIgnoreCase))
            return await HandleQueryRequestAsync(request, response, ct).ConfigureAwait(false);
        return HttpListenerLoop.ErrorResponse(response, 404, "Not Found", "Unknown endpoint");
    }

    private async Task<byte[]> HandleQueryRequestAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        string? queryString = null;

        if (request.HttpMethod == "GET")
        {
            queryString = request.QueryString["query"];
        }
        else if (request.HttpMethod == "POST")
        {
            var contentType = request.ContentType ?? "";

            if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                queryString = ParseFormParameter(body, "query");
            }
            else if (contentType.StartsWith("application/sparql-query", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                queryString = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            else
            {
                return HttpListenerLoop.ErrorResponse(response, 415, "Unsupported Media Type",
                    "Expected application/x-www-form-urlencoded or application/sparql-query");
            }
        }
        else
        {
            return HttpListenerLoop.ErrorResponse(response, 405, "Method Not Allowed",
                "Query endpoint accepts GET or POST");
        }

        if (string.IsNullOrWhiteSpace(queryString))
        {
            return ServiceDescription(request, response);
        }

        // Determine output format from Accept header
        var acceptHeader = request.Headers["Accept"] ?? "";

        // Execute query via facade
        var store = _storeFactory();
        var result = SparqlEngine.Query(store, queryString);

        if (!result.Success)
        {
            return HttpListenerLoop.ErrorResponse(response, 400, "Bad Request",
                result.ErrorMessage ?? "Query execution failed");
        }

        // CONSTRUCT/DESCRIBE return RDF graphs, SELECT/ASK return SPARQL results
        if (result.Kind is ExecutionResultKind.Construct or ExecutionResultKind.Describe)
        {
            var rdfFormat = RdfFormatNegotiator.FromAcceptHeader(
                acceptHeader.AsSpan(),
                RdfFormat.Turtle);
            response.ContentType = RdfFormatNegotiator.GetContentType(rdfFormat);

            return FormatGraphResult(result, rdfFormat);
        }
        else if (result.Kind == ExecutionResultKind.Ask)
        {
            var format = string.IsNullOrEmpty(acceptHeader)
                ? SparqlResultFormat.Json
                : SparqlResultFormatNegotiator.FromAcceptHeader(acceptHeader.AsSpan(), SparqlResultFormat.Json);
            response.ContentType = SparqlResultFormatNegotiator.GetContentType(format);

            return FormatAskResult(result, format);
        }
        else
        {
            var format = string.IsNullOrEmpty(acceptHeader)
                ? SparqlResultFormat.Json
                : SparqlResultFormatNegotiator.FromAcceptHeader(acceptHeader.AsSpan(), SparqlResultFormat.Json);
            response.ContentType = SparqlResultFormatNegotiator.GetContentType(format);

            return FormatSelectResult(result, format);
        }
    }

    #region Result Formatting

    /// <summary>
    /// A SELECT result in <paramref name="format"/>, through the one writer of each SPARQL results format.
    /// </summary>
    private static byte[] FormatSelectResult(QueryResult result, SparqlResultFormat format)
    {
        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            var variables = result.Variables ?? [];
            var rows = result.Rows ?? [];

            switch (format)
            {
                case SparqlResultFormat.Xml:
                    using (var xml = new SparqlXmlResultWriter(writer))
                    {
                        xml.WriteHead(variables);
                        foreach (var row in rows)
                            xml.WriteResult(row);
                        xml.WriteEnd();
                    }
                    break;
                case SparqlResultFormat.Csv:
                case SparqlResultFormat.Tsv:
                    using (var delimited = new SparqlCsvResultWriter(writer, useTsv: format == SparqlResultFormat.Tsv))
                    {
                        delimited.WriteHead(variables);
                        foreach (var row in rows)
                            delimited.WriteResult(row);
                        delimited.WriteEnd();
                    }
                    break;
                default:
                    using (var json = new SparqlJsonResultWriter(writer))
                    {
                        json.WriteHead(variables);
                        foreach (var row in rows)
                            json.WriteResult(row);
                        json.WriteEnd();
                    }
                    break;
            }
        }
        return memStream.ToArray();
    }

    /// <summary>
    /// An ASK result: the JSON or XML results document, or for CSV and TSV, which define no boolean form, the word.
    /// </summary>
    private static byte[] FormatAskResult(QueryResult result, SparqlResultFormat format)
    {
        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            var answer = result.AskResult == true;
            switch (format)
            {
                case SparqlResultFormat.Json:
                    using (var json = new SparqlJsonResultWriter(writer))
                        json.WriteBooleanResult(answer);
                    break;
                case SparqlResultFormat.Xml:
                    using (var xml = new SparqlXmlResultWriter(writer))
                        xml.WriteBooleanResult(answer);
                    break;
                default:
                    writer.Write(answer ? "true" : "false");
                    break;
            }
        }
        return memStream.ToArray();
    }

    /// <summary>
    /// A CONSTRUCT or DESCRIBE result in the negotiated format, any of the six, its triples in the default graph. The
    /// format is the one the Content-Type names: N-Quads, TriG and JSON-LD used to get N-Triples under their own type.
    /// </summary>
    private static byte[] FormatGraphResult(QueryResult result, RdfFormat rdfFormat)
    {
        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        using (var document = new RdfDocumentWriter(writer, rdfFormat))
        {
            foreach (var (s, p, o) in result.Triples ?? [])
                document.Write(s, p, o, ReadOnlySpan<char>.Empty);
        }
        return memStream.ToArray();
    }

    #endregion

    private async Task<byte[]> HandleUpdateRequestAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (request.HttpMethod != "POST")
        {
            return HttpListenerLoop.ErrorResponse(response, 405, "Method Not Allowed",
                "Update endpoint accepts POST only");
        }

        if (!_options.EnableUpdates)
        {
            return HttpListenerLoop.ErrorResponse(response, 403, "Forbidden",
                "Updates are disabled on this endpoint");
        }

        string? updateString = null;
        var contentType = request.ContentType ?? "";

        if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            updateString = ParseFormParameter(body, "update");
        }
        else if (contentType.StartsWith("application/sparql-update", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            updateString = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        else
        {
            return HttpListenerLoop.ErrorResponse(response, 415, "Unsupported Media Type",
                "Expected application/x-www-form-urlencoded or application/sparql-update");
        }

        if (string.IsNullOrWhiteSpace(updateString))
        {
            return HttpListenerLoop.ErrorResponse(response, 400, "Bad Request",
                "Missing update parameter");
        }

        var store = _storeFactory();
        var result = SparqlEngine.Update(store, updateString);

        if (result.Success)
        {
            response.StatusCode = 200;
            response.ContentType = "application/json";

            return Encoding.UTF8.GetBytes($"{{\"success\":true,\"affected\":{result.AffectedCount}}}");
        }
        return HttpListenerLoop.ErrorResponse(response, 400, "Bad Request", result.ErrorMessage ?? "Update failed");
    }

    private byte[] ServiceDescription(HttpListenerRequest request, HttpListenerResponse response)
    {
        response.ContentType = "text/turtle";
        response.StatusCode = 200;

        // The endpoint is the one the request reached: a server on several addresses has no one base URI, and a
        // listener's "+" or "*" is no IRI (ADR-065 D4).
        var baseUri = request.Url is { } url ? url.GetLeftPart(UriPartial.Authority) + "/" : _loop.BaseUris[0];

        var sb = new StringBuilder();
        sb.AppendLine("@prefix sd: <http://www.w3.org/ns/sparql-service-description#> .");
        sb.AppendLine("@prefix void: <http://rdfs.org/ns/void#> .");
        sb.AppendLine();
        sb.AppendLine($"<{baseUri}sparql> a sd:Service ;");
        sb.AppendLine("    sd:endpoint <sparql> ;");
        sb.AppendLine("    sd:supportedLanguage sd:SPARQL11Query ;");

        if (_options.EnableUpdates)
        {
            sb.AppendLine("    sd:supportedLanguage sd:SPARQL11Update ;");
        }

        // Supported SPARQL 1.1 features
        // See: https://www.w3.org/TR/sparql11-service-description/#sd-Feature
        sb.AppendLine("    sd:feature sd:DeresolvableURIs ;");
        sb.AppendLine("    sd:feature sd:UnionDefaultGraph ;");
        sb.AppendLine("    sd:feature sd:BasicFederatedQuery ;");

        // Extended features (custom extensions)
        // Property paths: ^, *, +, ?, /, |, !
        sb.AppendLine("    sd:extensionFunction <http://www.w3.org/ns/sparql-features#PropertyPaths> ;");
        // Subqueries: nested SELECT
        sb.AppendLine("    sd:extensionFunction <http://www.w3.org/ns/sparql-features#SubQueries> ;");
        // Aggregates: COUNT, SUM, AVG, MIN, MAX, GROUP_CONCAT, SAMPLE
        sb.AppendLine("    sd:extensionFunction <http://www.w3.org/ns/sparql-features#Aggregates> ;");
        // Negation: NOT EXISTS, MINUS
        sb.AppendLine("    sd:extensionFunction <http://www.w3.org/ns/sparql-features#Negation> ;");
        // Full-text search: text:match
        sb.AppendLine("    sd:extensionFunction <http://jena.apache.org/text#match> ;");

        // Result formats
        sb.AppendLine("    sd:resultFormat <http://www.w3.org/ns/formats/SPARQL_Results_JSON> ;");
        sb.AppendLine("    sd:resultFormat <http://www.w3.org/ns/formats/SPARQL_Results_XML> ;");
        sb.AppendLine("    sd:resultFormat <http://www.w3.org/ns/formats/SPARQL_Results_CSV> ;");
        sb.AppendLine("    sd:resultFormat <http://www.w3.org/ns/formats/SPARQL_Results_TSV> ;");

        // RDF output formats for CONSTRUCT/DESCRIBE
        sb.AppendLine("    sd:resultFormat <http://www.w3.org/ns/formats/Turtle> ;");
        sb.AppendLine("    sd:resultFormat <http://www.w3.org/ns/formats/N-Triples> ;");
        sb.AppendLine("    sd:resultFormat <http://www.w3.org/ns/formats/RDF_XML> .");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string? ParseFormParameter(string body, string name)
    {
        var pairs = body.Split('&');
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
            {
                return WebUtility.UrlDecode(parts[1]);
            }
        }
        return null;
    }

    public void Dispose() => _loop.Dispose();

    public ValueTask DisposeAsync() => _loop.DisposeAsync();
}

/// <summary>
/// Configuration options for SparqlHttpServer.
/// </summary>
public sealed class SparqlHttpServerOptions
{
    /// <summary>
    /// Whether to enable SPARQL Update operations.
    /// Default: false (read-only).
    /// </summary>
    public bool EnableUpdates { get; set; } = false;

    /// <summary>
    /// Whether to enable CORS headers for browser access.
    /// Default: true.
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// CORS origin value (Access-Control-Allow-Origin header).
    /// Default: "*" (allow all origins).
    /// </summary>
    public string CorsOrigin { get; set; } = "*";

    /// <summary>
    /// Maximum query execution time in milliseconds.
    /// Default: 30000 (30 seconds). Set to 0 for unlimited.
    /// </summary>
    public int QueryTimeoutMs { get; set; } = 30000;
}
