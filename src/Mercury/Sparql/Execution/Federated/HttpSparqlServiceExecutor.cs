using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>
    /// Creates a new HttpSparqlServiceExecutor with a default HttpClient.
    /// </summary>
    public HttpSparqlServiceExecutor()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/sparql-results+json");
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a new HttpSparqlServiceExecutor with the provided HttpClient.
    /// </summary>
    /// <param name="httpClient">HttpClient to use for requests (not disposed by this class).</param>
    public HttpSparqlServiceExecutor(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = false;
    }

    public async ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(
        string endpointUri,
        string query,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var requestUri = BuildRequestUri(endpointUri, query);

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseSelectResults(content);
        }
        catch (HttpRequestException ex)
        {
            throw new SparqlServiceException($"HTTP request to SPARQL endpoint failed: {ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
        catch (JsonException ex)
        {
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        var requestUri = BuildRequestUri(endpointUri, query);

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseAskResult(content);
        }
        catch (HttpRequestException ex)
        {
            throw new SparqlServiceException($"HTTP request to SPARQL endpoint failed: {ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
        catch (JsonException ex)
        {
            throw new SparqlServiceException($"Failed to parse SPARQL results JSON: {ex.Message}", ex)
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
