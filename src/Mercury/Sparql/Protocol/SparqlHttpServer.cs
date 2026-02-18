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
    private readonly HttpListener _listener;
    private readonly Func<QuadStore> _storeFactory;
    private readonly string _baseUri;
    private readonly SparqlHttpServerOptions _options;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new SPARQL HTTP server with a store factory.
    /// Each request resolves the store via the factory, enabling seamless store switching
    /// (e.g., after pruning) without restarting the server.
    /// </summary>
    /// <param name="storeFactory">Factory function that returns the current QuadStore.</param>
    /// <param name="baseUri">Base URI to listen on (e.g., "http://localhost:8080/").</param>
    /// <param name="options">Server configuration options.</param>
    public SparqlHttpServer(Func<QuadStore> storeFactory, string baseUri, SparqlHttpServerOptions? options = null)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        _options = options ?? new SparqlHttpServerOptions();

        if (!_baseUri.EndsWith("/"))
            _baseUri += "/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUri);
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

        // Execute query via facade
        var store = _storeFactory();
        var result = SparqlEngine.Query(store, queryString);

        if (!result.Success)
        {
            await WriteErrorResponseAsync(response, 400, "Bad Request",
                result.ErrorMessage ?? "Query execution failed").ConfigureAwait(false);
            return;
        }

        // CONSTRUCT/DESCRIBE return RDF graphs, SELECT/ASK return SPARQL results
        if (result.Kind is ExecutionResultKind.Construct or ExecutionResultKind.Describe)
        {
            var rdfFormat = RdfFormatNegotiator.FromAcceptHeader(
                acceptHeader.AsSpan(),
                RdfFormat.Turtle);
            response.ContentType = RdfFormatNegotiator.GetContentType(rdfFormat);

            var resultBytes = FormatGraphResult(result, rdfFormat);
            await response.OutputStream.WriteAsync(resultBytes, ct).ConfigureAwait(false);
        }
        else if (result.Kind == ExecutionResultKind.Ask)
        {
            var format = string.IsNullOrEmpty(acceptHeader)
                ? SparqlResultFormat.Json
                : SparqlResultFormatNegotiator.FromAcceptHeader(acceptHeader.AsSpan(), SparqlResultFormat.Json);
            response.ContentType = SparqlResultFormatNegotiator.GetContentType(format);

            var resultBytes = FormatAskResult(result, format);
            await response.OutputStream.WriteAsync(resultBytes, ct).ConfigureAwait(false);
        }
        else
        {
            var format = string.IsNullOrEmpty(acceptHeader)
                ? SparqlResultFormat.Json
                : SparqlResultFormatNegotiator.FromAcceptHeader(acceptHeader.AsSpan(), SparqlResultFormat.Json);
            response.ContentType = SparqlResultFormatNegotiator.GetContentType(format);

            var resultBytes = FormatSelectResult(result, format);
            await response.OutputStream.WriteAsync(resultBytes, ct).ConfigureAwait(false);
        }
    }

    #region Result Formatting

    private static byte[] FormatSelectResult(QueryResult result, SparqlResultFormat format)
    {
        using var memStream = new MemoryStream();
        using var writer = new StreamWriter(memStream, Encoding.UTF8, leaveOpen: true);

        var variables = result.Variables ?? [];
        var rows = result.Rows ?? [];

        switch (format)
        {
            case SparqlResultFormat.Json:
                WriteSelectJson(writer, variables, rows);
                break;
            case SparqlResultFormat.Xml:
                WriteSelectXml(writer, variables, rows);
                break;
            case SparqlResultFormat.Csv:
                WriteSelectDelimited(writer, variables, rows, ',');
                break;
            case SparqlResultFormat.Tsv:
                WriteSelectDelimited(writer, variables, rows, '\t');
                break;
            default:
                WriteSelectJson(writer, variables, rows);
                break;
        }

        writer.Flush();
        return memStream.ToArray();
    }

    private static byte[] FormatAskResult(QueryResult result, SparqlResultFormat format)
    {
        using var memStream = new MemoryStream();
        using var writer = new StreamWriter(memStream, Encoding.UTF8, leaveOpen: true);

        var boolStr = result.AskResult == true ? "true" : "false";

        switch (format)
        {
            case SparqlResultFormat.Json:
                writer.Write($"{{\"boolean\":{boolStr}}}");
                break;
            case SparqlResultFormat.Xml:
                writer.Write("<?xml version=\"1.0\"?>");
                writer.Write("<sparql xmlns=\"http://www.w3.org/2005/sparql-results#\">");
                writer.Write($"<boolean>{boolStr}</boolean>");
                writer.Write("</sparql>");
                break;
            default:
                writer.Write(boolStr);
                break;
        }

        writer.Flush();
        return memStream.ToArray();
    }

    private static byte[] FormatGraphResult(QueryResult result, RdfFormat rdfFormat)
    {
        using var memStream = new MemoryStream();
        using var writer = new StreamWriter(memStream, Encoding.UTF8, leaveOpen: true);

        // Fall back to NTriples for quad-only formats
        var format = rdfFormat switch
        {
            RdfFormat.NTriples or RdfFormat.Turtle or RdfFormat.RdfXml => rdfFormat,
            _ => RdfFormat.NTriples
        };
        RdfEngine.WriteTriples(writer, format, result.Triples ?? []);

        writer.Flush();
        return memStream.ToArray();
    }

    private static void WriteSelectJson(StreamWriter writer, string[] variables, List<Dictionary<string, string>> rows)
    {
        writer.Write("{\"head\":{\"vars\":[");
        for (int i = 0; i < variables.Length; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write($"\"{EscapeJson(variables[i])}\"");
        }
        writer.Write("]},\"results\":{\"bindings\":[");

        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) writer.Write(',');
            writer.Write('{');

            var row = rows[r];
            bool firstVar = true;
            foreach (var variable in variables)
            {
                if (row.TryGetValue(variable, out var valueStr))
                {
                    if (!firstVar) writer.Write(',');
                    firstVar = false;

                    var (type, val, extra) = ClassifyRdfTerm(valueStr);
                    writer.Write($"\"{EscapeJson(variable)}\":{{\"type\":\"{type}\",\"value\":\"{EscapeJson(val)}\"");
                    if (extra != null)
                    {
                        if (type == "literal" && extra.StartsWith("@"))
                            writer.Write($",\"xml:lang\":\"{extra.Substring(1)}\"");
                        else if (type == "literal" && extra.StartsWith("^^"))
                            writer.Write($",\"datatype\":\"{extra.Substring(2)}\"");
                    }
                    writer.Write('}');
                }
            }

            writer.Write('}');
        }

        writer.Write("]}}");
    }

    private static void WriteSelectXml(StreamWriter writer, string[] variables, List<Dictionary<string, string>> rows)
    {
        writer.Write("<?xml version=\"1.0\"?>");
        writer.Write("<sparql xmlns=\"http://www.w3.org/2005/sparql-results#\">");
        writer.Write("<head>");
        foreach (var v in variables)
            writer.Write($"<variable name=\"{EscapeXml(v)}\"/>");
        writer.Write("</head>");
        writer.Write("<results>");

        foreach (var row in rows)
        {
            writer.Write("<result>");
            foreach (var variable in variables)
            {
                if (row.TryGetValue(variable, out var valueStr))
                {
                    var (type, val, extra) = ClassifyRdfTerm(valueStr);
                    writer.Write($"<binding name=\"{EscapeXml(variable)}\">");

                    if (type == "uri")
                        writer.Write($"<uri>{EscapeXml(val)}</uri>");
                    else if (type == "bnode")
                        writer.Write($"<bnode>{EscapeXml(val)}</bnode>");
                    else
                    {
                        writer.Write("<literal");
                        if (extra != null && extra.StartsWith("@"))
                            writer.Write($" xml:lang=\"{extra.Substring(1)}\"");
                        else if (extra != null && extra.StartsWith("^^"))
                            writer.Write($" datatype=\"{extra.Substring(2)}\"");
                        writer.Write($">{EscapeXml(val)}</literal>");
                    }

                    writer.Write("</binding>");
                }
            }
            writer.Write("</result>");
        }

        writer.Write("</results>");
        writer.Write("</sparql>");
    }

    private static void WriteSelectDelimited(StreamWriter writer, string[] variables, List<Dictionary<string, string>> rows, char delimiter)
    {
        writer.WriteLine(string.Join(delimiter, variables));

        foreach (var row in rows)
        {
            var values = new string[variables.Length];
            for (int i = 0; i < variables.Length; i++)
            {
                values[i] = row.TryGetValue(variables[i], out var v) ? v : "";

                if (delimiter == ',' && (values[i].Contains(',') || values[i].Contains('"') || values[i].Contains('\n')))
                {
                    values[i] = "\"" + values[i].Replace("\"", "\"\"") + "\"";
                }
            }
            writer.WriteLine(string.Join(delimiter, values));
        }
    }

    private static (string type, string value, string? extra) ClassifyRdfTerm(string term)
    {
        if (term.StartsWith("<") && term.EndsWith(">"))
            return ("uri", term.Substring(1, term.Length - 2), null);

        if (term.StartsWith("_:"))
            return ("bnode", term.Substring(2), null);

        if (term.StartsWith("\""))
        {
            int closeQuote = term.LastIndexOf('"');
            if (closeQuote > 0)
            {
                var value = term.Substring(1, closeQuote - 1);
                var suffix = term.Substring(closeQuote + 1);
                return ("literal", value, suffix.Length > 0 ? suffix : null);
            }
        }

        return ("literal", term, null);
    }

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string EscapeXml(string s)
    {
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }

    #endregion

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

        var store = _storeFactory();
        var result = SparqlEngine.Update(store, updateString);

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
