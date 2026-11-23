using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Federated;

/// <summary>
/// Default implementation of ISparqlServiceExecutor using BCL HttpClient.
/// Parses SPARQL JSON Results Format (application/sparql-results+json).
/// </summary>
internal sealed class HttpSparqlServiceExecutor : ISparqlServiceExecutor, IDisposable
{
    /// <summary>How long a call waits to connect to its endpoint, unless it is told otherwise (ADR-066 D5).</summary>
    internal static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a call waits for its endpoint's answer, until ADR-060's deadline (ADR-066 D5).</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(100);

    /// <summary>How much of an endpoint's body a failure says (ADR-066 R2).</summary>
    private const int ReasonBytes = 4096;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _callTimeout;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>
    /// Creates a new HttpSparqlServiceExecutor with a client of its own, which connects within
    /// <see cref="DefaultConnectTimeout"/>.
    /// </summary>
    public HttpSparqlServiceExecutor() : this(DefaultConnectTimeout)
    {
    }

    /// <summary>
    /// Creates a new HttpSparqlServiceExecutor with a client of its own, which connects within
    /// <paramref name="connectTimeout"/>. The call's bound is the executor's own: the client has none.
    /// </summary>
    public HttpSparqlServiceExecutor(TimeSpan connectTimeout)
        : this(new HttpClient(new SocketsHttpHandler { ConnectTimeout = connectTimeout })
            { Timeout = Timeout.InfiniteTimeSpan }, CallTimeout)
    {
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/sparql-results+json");
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a new HttpSparqlServiceExecutor with the provided HttpClient.
    /// </summary>
    /// <param name="httpClient">HttpClient to use for requests (not disposed by this class).</param>
    public HttpSparqlServiceExecutor(HttpClient httpClient) : this(httpClient, CallTimeout)
    {
    }

    /// <summary>
    /// Creates a new HttpSparqlServiceExecutor with the provided HttpClient, each call bounded by
    /// <paramref name="callTimeout"/>.
    /// </summary>
    internal HttpSparqlServiceExecutor(HttpClient httpClient, TimeSpan callTimeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _callTimeout = callTimeout;
    }

    public async ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(
        string endpointUri,
        string query,
        CancellationToken ct = default)
    {
        var content = await GetResultsAsync(endpointUri, query, ct).ConfigureAwait(false);
        try
        {
            return ParseSelectResults(content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // An answer that is not JSON, or JSON of another shape: an element of the wrong kind, a member missing
            throw new SparqlServiceException($"Failed to parse SPARQL results JSON: {ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
    }

    public async ValueTask<bool> ExecuteAskAsync(
        string endpointUri,
        string query,
        CancellationToken ct = default)
    {
        var content = await GetResultsAsync(endpointUri, query, ct).ConfigureAwait(false);
        try
        {
            return ParseAskResult(content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // An answer that is not JSON, or JSON of another shape: an element of the wrong kind, a member missing
            throw new SparqlServiceException($"Failed to parse SPARQL results JSON: {ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
    }

    /// <summary>
    /// The endpoint's answer to <paramref name="query"/>. An answer that is not a success is said with the endpoint's
    /// reason, the first 4 KiB of its body (ADR-066 R2). A call that gets no answer says why: refused, not connected
    /// within the connect timeout, or not answered within the call's; not cancelled (ADR-065 Step 6, ADR-066 D5).
    /// </summary>
    private async Task<string> GetResultsAsync(string endpointUri, string query, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // An IRI that is not an HTTP endpoint cannot be asked: a call that fails (SPARQL 1.1 Federated Query §2.3)
        var requestUri = BuildRequestUri(endpointUri, query);
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SparqlServiceException($"The SERVICE IRI {endpointUri} is not an HTTP endpoint.")
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }

        // The call is bounded here, beside the query's token: the executor's own client has no timeout of its own
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(_callTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(uri, call.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The reason, read as UTF-8, cut at 4 KiB, and said to be cut
                var body = await response.Content.ReadAsByteArrayAsync(call.Token).ConfigureAwait(false);
                var reason = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, ReasonBytes));
                if (body.Length > ReasonBytes)
                    reason += $" [the first 4 KiB of {body.Length.ToString(CultureInfo.InvariantCulture)} bytes]";
                throw new SparqlServiceException(
                    $"The SPARQL endpoint {endpointUri} answered {(int)response.StatusCode} {response.ReasonPhrase}: {reason}")
                {
                    EndpointUri = endpointUri,
                    Query = query
                };
            }
            return await response.Content.ReadAsStringAsync(call.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SparqlServiceException($"HTTP request to SPARQL endpoint failed: {ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
        catch (InvalidOperationException ex)
        {
            // The request is an HTTP GET of an absolute HTTP IRI: what is refused here is the answer, read as text in a
            // character set that is not one
            throw new SparqlServiceException(
                $"The SPARQL endpoint {endpointUri} answered, and its answer cannot be read: {ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && call.IsCancellationRequested)
        {
            var seconds = ((long)_callTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            throw new SparqlServiceException($"The SPARQL endpoint {endpointUri} did not answer within {seconds} s.", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Neither the query nor the call's bound ended it: the client's handler did, with the one bound the
            // executor's own client has, its connect timeout, and its reason says so
            throw new SparqlServiceException(
                $"The SPARQL endpoint {endpointUri} could not be connected to: {ex.InnerException?.Message ?? ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
    }

    private static string BuildRequestUri(string endpointUri, string query)
    {
        var encodedQuery = WebUtility.UrlEncode(query);
        var separator = endpointUri.Contains('?') ? "&" : "?";
        return $"{endpointUri}{separator}query={encodedQuery}";
    }

    /// <summary>
    /// Parses SPARQL JSON Results Format for SELECT queries.
    /// Format: { "head": { "vars": [...] }, "results": { "bindings": [...] } }
    /// </summary>
    private static List<ServiceResultRow> ParseSelectResults(string json)
    {
        var results = new List<ServiceResultRow>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var resultsElement))
            return results;

        if (!resultsElement.TryGetProperty("bindings", out var bindingsElement))
            return results;

        foreach (var bindingElement in bindingsElement.EnumerateArray())
        {
            var row = new ServiceResultRow();

            foreach (var property in bindingElement.EnumerateObject())
            {
                var varName = property.Name;
                var valueObj = property.Value;

                var binding = ParseBinding(valueObj);
                row.AddBinding(varName, binding);
            }

            results.Add(row);
        }

        return results;
    }

    /// <summary>
    /// Parses a single binding value from SPARQL JSON Results.
    /// Format: { "type": "uri"|"literal"|"bnode", "value": "...", "xml:lang"?: "...", "datatype"?: "..." }
    /// </summary>
    private static ServiceBinding ParseBinding(JsonElement element)
    {
        var typeStr = element.GetProperty("type").GetString() ?? "literal";
        var value = element.GetProperty("value").GetString() ?? "";

        var type = typeStr switch
        {
            "uri" => ServiceBindingType.Uri,
            "bnode" => ServiceBindingType.BNode,
            _ => ServiceBindingType.Literal
        };

        string? datatype = null;
        string? language = null;

        if (type == ServiceBindingType.Literal)
        {
            if (element.TryGetProperty("xml:lang", out var langElement))
                language = langElement.GetString();
            else if (element.TryGetProperty("datatype", out var dtElement))
                datatype = dtElement.GetString();
        }

        return new ServiceBinding(type, value, datatype, language);
    }

    /// <summary>
    /// Parses SPARQL JSON Results Format for ASK queries.
    /// Format: { "head": { }, "boolean": true|false }
    /// </summary>
    private static bool ParseAskResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("boolean", out var boolElement))
            return boolElement.GetBoolean();

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
