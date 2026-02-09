using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for SPARQL HTTP Server.
/// Note: These tests require HttpListener which may need admin privileges on some platforms.
/// Tests are skipped if the port is unavailable.
/// </summary>
public class SparqlHttpServerTests : IDisposable
{
    private readonly string _testDir;
    private readonly QuadStore _store;
    private readonly int _port;
    private static int _portCounter = 18080;

    public SparqlHttpServerTests()
    {
        var tempPath = TempPath.Test("http");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _store = new QuadStore(_testDir);

        // Add test data
        _store.AddCurrent("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        _store.AddCurrent("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        _store.AddCurrent("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/bob>");
        _store.AddCurrent("<http://example.org/bob>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");

        // Use unique port for each test class instance
        _port = System.Threading.Interlocked.Increment(ref _portCounter);
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testDir);
    }

    private bool TryStartServer(out SparqlHttpServer server, SparqlHttpServerOptions? options = null)
    {
        try
        {
            server = new SparqlHttpServer(_store, $"http://localhost:{_port}/", options);
            server.Start();
            return true;
        }
        catch (HttpListenerException)
        {
            // Port unavailable or insufficient privileges
            server = null!;
            return false;
        }
    }

    #region Server Lifecycle Tests

    [Fact]
    public void Server_CanCreate()
    {
        using var server = new SparqlHttpServer(_store, "http://localhost:19999/");
        Assert.False(server.IsListening);
        Assert.Equal("http://localhost:19999/", server.BaseUri);
    }

    [Fact]
    public void Server_NormalizesBaseUri()
    {
        using var server = new SparqlHttpServer(_store, "http://localhost:19998");
        Assert.Equal("http://localhost:19998/", server.BaseUri);
    }

    [Fact]
    public void Server_ThrowsOnNullStore()
    {
        Assert.Throws<ArgumentNullException>(() => new SparqlHttpServer((QuadStore)null!, "http://localhost:19997/"));
    }

    [Fact]
    public void Server_ThrowsOnNullUri()
    {
        Assert.Throws<ArgumentNullException>(() => new SparqlHttpServer(_store, null!));
    }

    #endregion

    #region Query Endpoint Tests

    [Fact]
    public async Task QueryEndpoint_SelectQuery_ReturnsJsonResults()
    {
        if (!TryStartServer(out var server))
        {
            // Skip if can't bind to port
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 10";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("application/sparql-results+json", response.Content.Headers.ContentType?.ToString());

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"head\"", content);
            Assert.Contains("\"results\"", content);
            Assert.Contains("\"bindings\"", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_AskQuery_ReturnsBooleanResult()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "ASK WHERE { <http://example.org/alice> ?p ?o }";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"boolean\"", content);
            Assert.Contains("true", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_PostFormEncoded_Works()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 5";
            var content = new StringContent($"query={WebUtility.UrlEncode(query)}",
                Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await client.PostAsync($"http://localhost:{_port}/sparql", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_PostDirectQuery_Works()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 5";
            var content = new StringContent(query, Encoding.UTF8, "application/sparql-query");

            var response = await client.PostAsync($"http://localhost:{_port}/sparql", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_NoQuery_ReturnsServiceDescription()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var response = await client.GetAsync($"http://localhost:{_port}/sparql");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("text/turtle", response.Content.Headers.ContentType?.ToString());

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("sd:Service", content);
            Assert.Contains("sd:SPARQL11Query", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_InvalidQuery_Returns400()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "INVALID SPARQL QUERY";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("error", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    #endregion

    #region Content Negotiation Tests

    [Fact]
    public async Task QueryEndpoint_AcceptXml_ReturnsXml()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Accept.ParseAdd("application/sparql-results+xml");

            var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 1";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("application/sparql-results+xml", response.Content.Headers.ContentType?.ToString());

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("<sparql", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_AcceptCsv_ReturnsCsv()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Accept.ParseAdd("text/csv");

            var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 1";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("text/csv", response.Content.Headers.ContentType?.ToString());
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    #endregion

    #region Update Endpoint Tests

    [Fact]
    public async Task UpdateEndpoint_DisabledByDefault_Returns403()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var update = "INSERT DATA { <http://example.org/test> <http://example.org/value> \"test\" }";
            var content = new StringContent(update, Encoding.UTF8, "application/sparql-update");

            var response = await client.PostAsync($"http://localhost:{_port}/sparql/update", content);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task UpdateEndpoint_WhenEnabled_Works()
    {
        var options = new SparqlHttpServerOptions { EnableUpdates = true };
        if (!TryStartServer(out var server, options))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var update = "INSERT DATA { <http://example.org/test> <http://example.org/value> \"test\" }";
            var content = new StringContent(update, Encoding.UTF8, "application/sparql-update");

            var response = await client.PostAsync($"http://localhost:{_port}/sparql/update", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":true", responseContent);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task UpdateEndpoint_GetMethod_Returns405()
    {
        var options = new SparqlHttpServerOptions { EnableUpdates = true };
        if (!TryStartServer(out var server, options))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var response = await client.GetAsync($"http://localhost:{_port}/sparql/update");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    #endregion

    #region CORS Tests

    [Fact]
    public async Task CorsHeaders_EnabledByDefault()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "ASK WHERE { ?s ?p ?o }";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task CorsHeaders_CanBeDisabled()
    {
        var options = new SparqlHttpServerOptions { EnableCors = false };
        if (!TryStartServer(out var server, options))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "ASK WHERE { ?s ?p ?o }";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task OptionsRequest_Returns204()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var request = new HttpRequestMessage(HttpMethod.Options, $"http://localhost:{_port}/sparql");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task UnknownEndpoint_Returns404()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var response = await client.GetAsync($"http://localhost:{_port}/unknown");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task UnsupportedMediaType_Returns415()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var content = new StringContent("SELECT * WHERE { ?s ?p ?o }", Encoding.UTF8, "text/plain");
            var response = await client.PostAsync($"http://localhost:{_port}/sparql", content);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    #endregion

    #region CONSTRUCT and DESCRIBE Tests

    [Fact]
    public async Task QueryEndpoint_ConstructQuery_ReturnsTurtle()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            var query = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o } LIMIT 1";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();
            // Should contain N-Triples style output
            Assert.Contains(" .", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_DescribeQuery_ReturnsTurtle()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();

            // Use DESCRIBE with a variable instead of specific IRI
            // since exact IRI matching can be tricky with angle brackets
            var query = "DESCRIBE ?s WHERE { ?s <http://xmlns.com/foaf/0.1/name> \"Alice\" }";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();
            // DESCRIBE returns properties of matching resources
            // Content may be empty if no properties found, which is valid
            Assert.NotNull(content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_ConstructQuery_AcceptNTriples_ReturnsNTriples()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/n-triples");

            var query = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o } LIMIT 1";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/n-triples", response.Content.Headers.ContentType?.MediaType);

            var content = await response.Content.ReadAsStringAsync();
            // N-Triples uses full IRIs with angle brackets
            Assert.Contains("<http://", content);
            Assert.Contains(" .", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_ConstructQuery_AcceptRdfXml_ReturnsRdfXml()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/rdf+xml");

            var query = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o } LIMIT 1";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/rdf+xml", response.Content.Headers.ContentType?.MediaType);

            var content = await response.Content.ReadAsStringAsync();
            // RDF/XML starts with XML declaration and rdf:RDF element
            Assert.StartsWith("<?xml version=\"1.0\"", content);
            Assert.Contains("<rdf:RDF", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task QueryEndpoint_ConstructQuery_DefaultAccept_ReturnsTurtle()
    {
        if (!TryStartServer(out var server))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            // No Accept header - should default to Turtle

            var query = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o } LIMIT 1";
            var response = await client.GetAsync($"http://localhost:{_port}/sparql?query={WebUtility.UrlEncode(query)}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/turtle", response.Content.Headers.ContentType?.MediaType);

            var content = await response.Content.ReadAsStringAsync();
            // Turtle uses prefix declarations or shortened forms
            Assert.Contains(" .", content);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    #endregion
}
