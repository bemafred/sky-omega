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
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Results;
using SkyOmega.Mercury.Storage;
using RdfFormat = SkyOmega.Mercury.Rdf.RdfFormat;
using RdfFormatNegotiator = SkyOmega.Mercury.Rdf.RdfFormatNegotiator;

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
    private readonly HttpListener _listener;
    private readonly QuadStore _store;
    private readonly string _baseUri;
    private readonly SparqlHttpServerOptions _options;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new SPARQL HTTP server.
    /// </summary>
    /// <param name="store">The QuadStore to query and update.</param>
    /// <param name="baseUri">Base URI to listen on (e.g., "http://localhost:8080/").</param>
    /// <param name="options">Server configuration options.</param>
    public SparqlHttpServer(QuadStore store, string baseUri, SparqlHttpServerOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        _options = options ?? new SparqlHttpServerOptions();

        if (!_baseUri.EndsWith("/"))
            _baseUri += "/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUri);
    }

    /// <summary>
    /// Start the HTTP server and begin accepting requests.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _cts = new CancellationTokenSource();
        _listener.Start();
        _listenTask = AcceptConnectionsAsync(_cts.Token);
    }

    /// <summary>
    /// Stop the HTTP server gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _listener.Stop();

            if (_listenTask != null)
            {
                try
                {
                    await _listenTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown
                }
            }
        }
    }

    /// <summary>
    /// Whether the server is currently running.
    /// </summary>
    public bool IsListening => _listener.IsListening;

    /// <summary>
    /// The base URI the server is listening on.
    /// </summary>
    public string BaseUri => _baseUri;

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Handle request in background (don't await)
                _ = HandleRequestAsync(context, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
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
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";

            if (path.EndsWith("/sparql/update", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUpdateRequestAsync(request, response, ct).ConfigureAwait(false);
            }
            else if (path.EndsWith("/sparql", StringComparison.OrdinalIgnoreCase))
            {
                await HandleQueryRequestAsync(request, response, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteErrorResponseAsync(response, 404, "Not Found", "Unknown endpoint").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            try
            {
                await WriteErrorResponseAsync(response, 500, "Internal Server Error", ex.Message).ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors writing error response
            }
        }
        finally
        {
            response.Close();
        }
    }

    private async Task HandleQueryRequestAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
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
                await WriteErrorResponseAsync(response, 415, "Unsupported Media Type",
                    "Expected application/x-www-form-urlencoded or application/sparql-query").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            await WriteErrorResponseAsync(response, 405, "Method Not Allowed",
                "Query endpoint accepts GET or POST").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(queryString))
        {
            await WriteServiceDescriptionAsync(response, ct).ConfigureAwait(false);
            return;
        }

        // Determine output format from Accept header
        var acceptHeader = request.Headers["Accept"] ?? "";

        // Parse and execute query
        try
        {
            var parser = new SparqlParser(queryString.AsSpan());
            var query = parser.ParseQuery();

            // CONSTRUCT/DESCRIBE return RDF graphs, SELECT/ASK return SPARQL results
            if (query.Type == QueryType.Construct || query.Type == QueryType.Describe)
            {
                // Use RDF format negotiation for graph results
                var rdfFormat = RdfFormatNegotiator.FromAcceptHeader(
                    acceptHeader.AsSpan(),
                    RdfFormat.Turtle); // Default to Turtle for readability
                response.ContentType = RdfFormatNegotiator.GetContentType(rdfFormat);

                var resultBytes = ExecuteGraphQuerySync(queryString, query, rdfFormat);
                await response.OutputStream.WriteAsync(resultBytes, ct).ConfigureAwait(false);
            }
            else
            {
                // Use SPARQL result format negotiation for SELECT/ASK
                var format = string.IsNullOrEmpty(acceptHeader)
                    ? SparqlResultFormat.Json
                    : SparqlResultFormatNegotiator.FromAcceptHeader(acceptHeader.AsSpan(), SparqlResultFormat.Json);
                response.ContentType = SparqlResultFormatNegotiator.GetContentType(format);

                var resultBytes = ExecuteQuerySync(queryString, query, format);
                await response.OutputStream.WriteAsync(resultBytes, ct).ConfigureAwait(false);
            }
        }
        catch (SparqlParseException ex)
        {
            await WriteErrorResponseAsync(response, 400, "Bad Request",
                $"SPARQL parse error: {ex.Message}").ConfigureAwait(false);
        }
    }

    private byte[] ExecuteQuerySync(string queryString, Query query, SparqlResultFormat format)
    {
        using var memStream = new MemoryStream();
        using var writer = new StreamWriter(memStream, Encoding.UTF8, leaveOpen: true);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, queryString.AsSpan(), query);

            switch (query.Type)
            {
                case QueryType.Select:
                    WriteSelectResults(executor, query, queryString, format, writer);
                    break;

                case QueryType.Ask:
                    WriteAskResult(executor, format, writer);
                    break;

                default:
                    throw new InvalidOperationException($"Query type {query.Type} should use ExecuteGraphQuerySync");
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        writer.Flush();
        return memStream.ToArray();
    }

    private byte[] ExecuteGraphQuerySync(string queryString, Query query, RdfFormat rdfFormat)
    {
        using var memStream = new MemoryStream();
        using var writer = new StreamWriter(memStream, Encoding.UTF8, leaveOpen: true);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, queryString.AsSpan(), query);

            switch (query.Type)
            {
                case QueryType.Construct:
                    WriteConstructResults(executor, rdfFormat, writer);
                    break;

                case QueryType.Describe:
                    WriteDescribeResults(executor, rdfFormat, writer);
                    break;

                default:
                    throw new InvalidOperationException($"Query type {query.Type} should use ExecuteQuerySync");
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        writer.Flush();
        return memStream.ToArray();
    }

    private void WriteSelectResults(QueryExecutor executor, Query query, string source,
        SparqlResultFormat format, StreamWriter writer)
    {
        // Extract variable names from patterns
        var varNames = ExtractVariableNames(query, source);

        var results = executor.Execute();

        try
        {
            using var resultWriter = SparqlResultFormatNegotiator.CreateWriter(writer, format);

            if (resultWriter is SparqlJsonResultWriter jsonWriter)
            {
                // If SELECT * and no explicit variables, get them from first result
                if (varNames.Length == 0 && results.MoveNext())
                {
                    varNames = ExtractVariablesFromBindings(results.Current, source);
                    jsonWriter.WriteHead(varNames);

                    var bindings = results.Current;
                    jsonWriter.WriteResult(ref bindings);

                    while (results.MoveNext())
                    {
                        var b = results.Current;
                        jsonWriter.WriteResult(ref b);
                    }
                }
                else
                {
                    jsonWriter.WriteHead(varNames);

                    while (results.MoveNext())
                    {
                        var bindings = results.Current;
                        jsonWriter.WriteResult(ref bindings);
                    }
                }

                jsonWriter.WriteEnd();
            }
            else if (resultWriter is SparqlXmlResultWriter xmlWriter)
            {
                if (varNames.Length == 0 && results.MoveNext())
                {
                    varNames = ExtractVariablesFromBindings(results.Current, source);
                    xmlWriter.WriteHead(varNames);
                    var bindings = results.Current;
                    xmlWriter.WriteResult(ref bindings);

                    while (results.MoveNext())
                    {
                        var b = results.Current;
                        xmlWriter.WriteResult(ref b);
                    }
                }
                else
                {
                    xmlWriter.WriteHead(varNames);
                    while (results.MoveNext())
                    {
                        var bindings = results.Current;
                        xmlWriter.WriteResult(ref bindings);
                    }
                }

                xmlWriter.WriteEnd();
            }
            else if (resultWriter is SparqlCsvResultWriter csvWriter)
            {
                if (varNames.Length == 0 && results.MoveNext())
                {
                    varNames = ExtractVariablesFromBindings(results.Current, source);
                    csvWriter.WriteHead(varNames);
                    var bindings = results.Current;
                    csvWriter.WriteResult(ref bindings);

                    while (results.MoveNext())
                    {
                        var b = results.Current;
                        csvWriter.WriteResult(ref b);
                    }
                }
                else
                {
                    csvWriter.WriteHead(varNames);
                    while (results.MoveNext())
                    {
                        var bindings = results.Current;
                        csvWriter.WriteResult(ref bindings);
                    }
                }

                csvWriter.WriteEnd();
            }
        }
        finally
        {
            results.Dispose();
        }
    }

    private static string[] ExtractVariableNames(Query query, string source)
    {
        // Check SelectClause for explicit variables
        if (!query.SelectClause.SelectAll)
        {
            // Check aggregates for aliases
            var vars = new List<string>();
            for (int i = 0; i < query.SelectClause.AggregateCount; i++)
            {
                var agg = query.SelectClause.GetAggregate(i);
                if (agg.AliasLength > 0)
                {
                    var alias = source.AsSpan().Slice(agg.AliasStart, agg.AliasLength).ToString();
                    // Remove leading ? if present
                    vars.Add(alias.StartsWith("?") ? alias.Substring(1) : alias);
                }
            }
            if (vars.Count > 0)
                return vars.ToArray();
        }

        // For SELECT * or when we can't determine variables, return empty
        // The caller should extract from first result bindings
        return [];
    }

    private static string[] ExtractVariablesFromBindings(BindingTable bindings, string source)
    {
        // BindingTable only stores hashes, not names
        // We need to scan the source for variables and match by hash
        var knownVars = new List<(string Name, int Hash)>();

        // Simple regex-like scan for ?varName patterns
        var span = source.AsSpan();
        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == '?')
            {
                int start = i;
                int end = i + 1;
                while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
                    end++;

                if (end > start + 1)
                {
                    var varName = span.Slice(start, end - start).ToString();
                    var hash = ComputeHash(varName.AsSpan());
                    if (!knownVars.Exists(v => v.Hash == hash))
                        knownVars.Add((varName.Substring(1), hash)); // Strip leading ?
                }
            }
        }

        // Match against bindings
        var result = new List<string>();
        for (int i = 0; i < bindings.Count; i++)
        {
            var bindingHash = bindings.GetVariableHash(i);
            var found = knownVars.Find(v => v.Hash == bindingHash);
            if (found.Name != null)
                result.Add(found.Name);
            else
                result.Add($"var{i}"); // Fallback
        }

        return result.ToArray();
    }

    private static int ComputeHash(ReadOnlySpan<char> name)
    {
        // Must match BindingTable.ComputeHash
        int hash = 17;
        foreach (var c in name)
            hash = hash * 31 + c;
        return hash;
    }

    private static void WriteAskResult(QueryExecutor executor, SparqlResultFormat format, StreamWriter writer)
    {
        bool result = executor.ExecuteAsk();

        using var resultWriter = SparqlResultFormatNegotiator.CreateWriter(writer, format);

        if (resultWriter is SparqlJsonResultWriter jsonWriter)
        {
            jsonWriter.WriteBooleanResult(result);
        }
        else if (resultWriter is SparqlXmlResultWriter xmlWriter)
        {
            xmlWriter.WriteBooleanResult(result);
        }
        else if (resultWriter is SparqlCsvResultWriter csvWriter)
        {
            // CSV doesn't support boolean results well, write as simple value
            writer.WriteLine(result ? "true" : "false");
        }
    }

    private static void WriteConstructResults(QueryExecutor executor, RdfFormat rdfFormat, StreamWriter writer)
    {
        var results = executor.ExecuteConstruct();

        try
        {
            switch (rdfFormat)
            {
                case RdfFormat.NTriples:
                    {
                        using var ntWriter = new NTriplesStreamWriter(writer);
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            ntWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                    }
                    break;

                case RdfFormat.Turtle:
                    {
                        using var turtleWriter = new TurtleStreamWriter(writer);
                        turtleWriter.WritePrefixes();
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            turtleWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                    }
                    break;

                case RdfFormat.RdfXml:
                    {
                        using var rdfXmlWriter = new RdfXmlStreamWriter(writer);
                        rdfXmlWriter.WriteStartDocument();
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            rdfXmlWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                        rdfXmlWriter.WriteEndDocument();
                    }
                    break;

                default:
                    // Default to N-Triples for unknown/unsupported formats
                    {
                        using var defaultWriter = new NTriplesStreamWriter(writer);
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            defaultWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                    }
                    break;
            }
        }
        finally
        {
            results.Dispose();
        }
    }

    private static void WriteDescribeResults(QueryExecutor executor, RdfFormat rdfFormat, StreamWriter writer)
    {
        var results = executor.ExecuteDescribe();

        try
        {
            switch (rdfFormat)
            {
                case RdfFormat.NTriples:
                    {
                        using var ntWriter = new NTriplesStreamWriter(writer);
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            ntWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                    }
                    break;

                case RdfFormat.Turtle:
                    {
                        using var turtleWriter = new TurtleStreamWriter(writer);
                        turtleWriter.WritePrefixes();
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            turtleWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                    }
                    break;

                case RdfFormat.RdfXml:
                    {
                        using var rdfXmlWriter = new RdfXmlStreamWriter(writer);
                        rdfXmlWriter.WriteStartDocument();
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            rdfXmlWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                        rdfXmlWriter.WriteEndDocument();
                    }
                    break;

                default:
                    // Default to N-Triples for unknown/unsupported formats
                    {
                        using var defaultWriter = new NTriplesStreamWriter(writer);
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            defaultWriter.WriteTriple(
                                triple.Subject.ToString().AsSpan(),
                                triple.Predicate.ToString().AsSpan(),
                                triple.Object.ToString().AsSpan());
                        }
                    }
                    break;
            }
        }
        finally
        {
            results.Dispose();
        }
    }

    private async Task HandleUpdateRequestAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (request.HttpMethod != "POST")
        {
            await WriteErrorResponseAsync(response, 405, "Method Not Allowed",
                "Update endpoint accepts POST only").ConfigureAwait(false);
            return;
        }

        if (!_options.EnableUpdates)
        {
            await WriteErrorResponseAsync(response, 403, "Forbidden",
                "Updates are disabled on this endpoint").ConfigureAwait(false);
            return;
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
            await WriteErrorResponseAsync(response, 415, "Unsupported Media Type",
                "Expected application/x-www-form-urlencoded or application/sparql-update").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(updateString))
        {
            await WriteErrorResponseAsync(response, 400, "Bad Request",
                "Missing update parameter").ConfigureAwait(false);
            return;
        }

        try
        {
            var parser = new SparqlParser(updateString.AsSpan());
            var operation = parser.ParseUpdate();

            var executor = new UpdateExecutor(_store, updateString.AsSpan(), operation);
            var result = executor.Execute();

            if (result.Success)
            {
                response.StatusCode = 200;
                response.ContentType = "application/json";

                var responseBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"affected\":{result.AffectedCount}}}");
                await response.OutputStream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteErrorResponseAsync(response, 400, "Bad Request",
                    result.ErrorMessage ?? "Update failed").ConfigureAwait(false);
            }
        }
        catch (SparqlParseException ex)
        {
            await WriteErrorResponseAsync(response, 400, "Bad Request",
                $"SPARQL parse error: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task WriteServiceDescriptionAsync(HttpListenerResponse response, CancellationToken ct)
    {
        response.ContentType = "text/turtle";
        response.StatusCode = 200;

        var sb = new StringBuilder();
        sb.AppendLine("@prefix sd: <http://www.w3.org/ns/sparql-service-description#> .");
        sb.AppendLine("@prefix void: <http://rdfs.org/ns/void#> .");
        sb.AppendLine();
        sb.AppendLine($"<{_baseUri}sparql> a sd:Service ;");
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

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteErrorResponseAsync(HttpListenerResponse response, int statusCode,
        string statusDescription, string message)
    {
        response.StatusCode = statusCode;
        response.StatusDescription = statusDescription;
        response.ContentType = "application/json";

        var escapedMessage = message
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");

        var bytes = Encoding.UTF8.GetBytes($"{{\"error\":\"{statusDescription}\",\"message\":\"{escapedMessage}\"}}");
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _listener.Close();
        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _cts?.Dispose();
    }
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
