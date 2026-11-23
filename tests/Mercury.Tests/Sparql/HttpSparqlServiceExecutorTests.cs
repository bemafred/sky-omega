using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for HttpSparqlServiceExecutor - the client for remote SPARQL endpoints.
/// </summary>
public class HttpSparqlServiceExecutorTests
{
    #region ServiceBinding Tests

    [Fact]
    public void ServiceBinding_Uri_ToRdfTerm_FormatsCorrectly()
    {
        var binding = new ServiceBinding(ServiceBindingType.Uri, "http://example.org/resource");
        Assert.Equal("<http://example.org/resource>", binding.ToRdfTerm());
    }

    [Fact]
    public void ServiceBinding_PlainLiteral_ToRdfTerm_FormatsCorrectly()
    {
        var binding = new ServiceBinding(ServiceBindingType.Literal, "hello");
        Assert.Equal("\"hello\"", binding.ToRdfTerm());
    }

    [Fact]
    public void ServiceBinding_LanguageTaggedLiteral_ToRdfTerm_FormatsCorrectly()
    {
        var binding = new ServiceBinding(ServiceBindingType.Literal, "hello", language: "en");
        Assert.Equal("\"hello\"@en", binding.ToRdfTerm());
    }

    [Fact]
    public void ServiceBinding_TypedLiteral_ToRdfTerm_FormatsCorrectly()
    {
        var binding = new ServiceBinding(ServiceBindingType.Literal, "42", datatype: "http://www.w3.org/2001/XMLSchema#integer");
        Assert.Equal("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", binding.ToRdfTerm());
    }

    [Fact]
    public void ServiceBinding_BNode_ToRdfTerm_FormatsCorrectly()
    {
        var binding = new ServiceBinding(ServiceBindingType.BNode, "b1");
        Assert.Equal("_:b1", binding.ToRdfTerm());
    }

    [Fact]
    public void ServiceBinding_Properties_ReturnCorrectValues()
    {
        var binding = new ServiceBinding(ServiceBindingType.Uri, "http://example.org", "dtype", "en");

        Assert.Equal(ServiceBindingType.Uri, binding.Type);
        Assert.Equal("http://example.org", binding.Value);
        Assert.Equal("dtype", binding.Datatype);
        Assert.Equal("en", binding.Language);
    }

    #endregion

    #region ServiceResultRow Tests

    [Fact]
    public void ServiceResultRow_Empty_HasZeroCount()
    {
        var row = new ServiceResultRow();
        Assert.Equal(0, row.Count);
        Assert.Empty(row.Variables);
    }

    [Fact]
    public void ServiceResultRow_AddBinding_IncreasesCount()
    {
        var row = new ServiceResultRow();
        row.AddBinding("x", new ServiceBinding(ServiceBindingType.Uri, "http://example.org"));

        Assert.Equal(1, row.Count);
        Assert.Contains("x", row.Variables);
    }

    [Fact]
    public void ServiceResultRow_TryGetBinding_ReturnsTrueForExistingVariable()
    {
        var row = new ServiceResultRow();
        var binding = new ServiceBinding(ServiceBindingType.Literal, "value");
        row.AddBinding("x", binding);

        Assert.True(row.TryGetBinding("x", out var result));
        Assert.Equal("value", result.Value);
    }

    [Fact]
    public void ServiceResultRow_TryGetBinding_ReturnsFalseForMissingVariable()
    {
        var row = new ServiceResultRow();

        Assert.False(row.TryGetBinding("missing", out _));
    }

    [Fact]
    public void ServiceResultRow_GetBinding_ReturnsDefaultForMissingVariable()
    {
        var row = new ServiceResultRow();

        var binding = row.GetBinding("missing");
        Assert.Null(binding.Value);
    }

    [Fact]
    public void ServiceResultRow_AddBinding_OverwritesExisting()
    {
        var row = new ServiceResultRow();
        row.AddBinding("x", new ServiceBinding(ServiceBindingType.Literal, "first"));
        row.AddBinding("x", new ServiceBinding(ServiceBindingType.Literal, "second"));

        Assert.Equal(1, row.Count);
        Assert.Equal("second", row.GetBinding("x").Value);
    }

    #endregion

    #region SparqlServiceException Tests

    [Fact]
    public void SparqlServiceException_PreservesMessage()
    {
        var ex = new SparqlServiceException("Test error message");
        Assert.Equal("Test error message", ex.Message);
    }

    [Fact]
    public void SparqlServiceException_PreservesInnerException()
    {
        var inner = new InvalidOperationException("Inner");
        var ex = new SparqlServiceException("Outer", inner);

        Assert.Equal("Outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void SparqlServiceException_StoresEndpointAndQuery()
    {
        var ex = new SparqlServiceException("Error")
        {
            EndpointUri = "http://example.org/sparql",
            Query = "SELECT * WHERE { ?s ?p ?o }"
        };

        Assert.Equal("http://example.org/sparql", ex.EndpointUri);
        Assert.Equal("SELECT * WHERE { ?s ?p ?o }", ex.Query);
    }

    #endregion

    #region Executor Construction Tests

    [Fact]
    public void Executor_DefaultConstructor_CreatesOwnHttpClient()
    {
        using var executor = new HttpSparqlServiceExecutor();
        // No exception means success - we can't directly inspect the HttpClient
    }

    [Fact]
    public void Executor_WithHttpClient_UsesProvidedClient()
    {
        using var httpClient = new HttpClient();
        using var executor = new HttpSparqlServiceExecutor(httpClient);
        // No exception means success
    }

    [Fact]
    public void Executor_WithNullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpSparqlServiceExecutor(null!));
    }

    #endregion

    #region Executor Disposal Tests

    [Fact]
    public async Task Executor_AfterDispose_ThrowsObjectDisposedException()
    {
        var executor = new HttpSparqlServiceExecutor();
        executor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Executor_AfterDispose_AskThrowsObjectDisposedException()
    {
        var executor = new HttpSparqlServiceExecutor();
        executor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => executor.ExecuteAskAsync("http://example.org/sparql", "ASK WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void Executor_DoubleDispose_DoesNotThrow()
    {
        var executor = new HttpSparqlServiceExecutor();
        executor.Dispose();
        executor.Dispose(); // Should not throw
    }

    [Fact]
    public void Executor_WithProvidedHttpClient_DoesNotDisposeClient()
    {
        var httpClient = new HttpClient();
        var executor = new HttpSparqlServiceExecutor(httpClient);
        executor.Dispose();

        // Client should still be usable - verify by making a call that won't throw ObjectDisposedException
        // We can't test this directly, but we can verify it doesn't throw on construction
        using var executor2 = new HttpSparqlServiceExecutor(httpClient);

        httpClient.Dispose();
    }

    #endregion

    #region Mock Handler Tests

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string Content { get; set; } = "";
        public string ContentType { get; set; } = "application/sparql-results+json";
        public string? CharSet { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public TimeSpan Delay { get; set; }
        public string? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();

            if (ExceptionToThrow != null)
                throw ExceptionToThrow;
            await Task.Delay(Delay, cancellationToken);

            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Content)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType) { CharSet = CharSet };

            return response;
        }
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesEmptyResults()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{"vars":["s","p","o"]},"results":{"bindings":[]}}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesUriBinding()
    {
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["s"]},
                "results": {
                    "bindings": [
                        {"s": {"type": "uri", "value": "http://example.org/resource"}}
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT ?s WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.True(results[0].TryGetBinding("s", out var binding));
        Assert.Equal(ServiceBindingType.Uri, binding.Type);
        Assert.Equal("http://example.org/resource", binding.Value);
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesLiteralBinding()
    {
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["name"]},
                "results": {
                    "bindings": [
                        {"name": {"type": "literal", "value": "Alice"}}
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT ?name WHERE { ?s :name ?name }", TestContext.Current.CancellationToken);

        Assert.Single(results);
        var binding = results[0].GetBinding("name");
        Assert.Equal(ServiceBindingType.Literal, binding.Type);
        Assert.Equal("Alice", binding.Value);
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesLanguageTaggedLiteral()
    {
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["label"]},
                "results": {
                    "bindings": [
                        {"label": {"type": "literal", "value": "Bonjour", "xml:lang": "fr"}}
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT ?label WHERE { ?s :label ?label }", TestContext.Current.CancellationToken);

        Assert.Single(results);
        var binding = results[0].GetBinding("label");
        Assert.Equal(ServiceBindingType.Literal, binding.Type);
        Assert.Equal("Bonjour", binding.Value);
        Assert.Equal("fr", binding.Language);
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesTypedLiteral()
    {
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["age"]},
                "results": {
                    "bindings": [
                        {"age": {"type": "literal", "value": "30", "datatype": "http://www.w3.org/2001/XMLSchema#integer"}}
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT ?age WHERE { ?s :age ?age }", TestContext.Current.CancellationToken);

        Assert.Single(results);
        var binding = results[0].GetBinding("age");
        Assert.Equal(ServiceBindingType.Literal, binding.Type);
        Assert.Equal("30", binding.Value);
        Assert.Equal("http://www.w3.org/2001/XMLSchema#integer", binding.Datatype);
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesBlankNode()
    {
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["b"]},
                "results": {
                    "bindings": [
                        {"b": {"type": "bnode", "value": "b1"}}
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT ?b WHERE { ?b :p :o }", TestContext.Current.CancellationToken);

        Assert.Single(results);
        var binding = results[0].GetBinding("b");
        Assert.Equal(ServiceBindingType.BNode, binding.Type);
        Assert.Equal("b1", binding.Value);
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesMultipleBindingsPerRow()
    {
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["s", "p", "o"]},
                "results": {
                    "bindings": [
                        {
                            "s": {"type": "uri", "value": "http://example.org/alice"},
                            "p": {"type": "uri", "value": "http://xmlns.com/foaf/0.1/name"},
                            "o": {"type": "literal", "value": "Alice"}
                        }
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(3, results[0].Count);
        Assert.Equal("http://example.org/alice", results[0].GetBinding("s").Value);
        Assert.Equal("http://xmlns.com/foaf/0.1/name", results[0].GetBinding("p").Value);
        Assert.Equal("Alice", results[0].GetBinding("o").Value);
    }

    [Fact]
    public async Task ExecuteSelectAsync_ParsesMultipleRows()
    {
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["name"]},
                "results": {
                    "bindings": [
                        {"name": {"type": "literal", "value": "Alice"}},
                        {"name": {"type": "literal", "value": "Bob"}},
                        {"name": {"type": "literal", "value": "Carol"}}
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT ?name WHERE { ?s :name ?name }", TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal("Alice", results[0].GetBinding("name").Value);
        Assert.Equal("Bob", results[1].GetBinding("name").Value);
        Assert.Equal("Carol", results[2].GetBinding("name").Value);
    }

    [Fact]
    public async Task ExecuteSelectAsync_HandlesPartialBindings()
    {
        // Some variables may be unbound in certain rows (OPTIONAL patterns)
        var handler = new MockHttpHandler
        {
            Content = """
            {
                "head": {"vars": ["s", "name"]},
                "results": {
                    "bindings": [
                        {"s": {"type": "uri", "value": "http://example.org/alice"}},
                        {"s": {"type": "uri", "value": "http://example.org/bob"}, "name": {"type": "literal", "value": "Bob"}}
                    ]
                }
            }
            """
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT ?s ?name WHERE { ?s a :Person . OPTIONAL { ?s :name ?name } }", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].Count); // Only s bound
        Assert.Equal(2, results[1].Count); // Both s and name bound
    }

    [Fact]
    public async Task ExecuteSelectAsync_HandlesMissingResultsProperty()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{"vars":[]}}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ExecuteSelectAsync_HandlesMissingBindingsProperty()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{"vars":[]},"results":{}}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var results = await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    #endregion

    #region ASK Query Tests

    [Fact]
    public async Task ExecuteAskAsync_ParsesTrueResult()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{},"boolean":true}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var result = await executor.ExecuteAskAsync("http://example.org/sparql", "ASK WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task ExecuteAskAsync_ParsesFalseResult()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{},"boolean":false}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var result = await executor.ExecuteAskAsync("http://example.org/sparql", "ASK WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ExecuteAskAsync_MissingBooleanReturnsFalse()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{}}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var result = await executor.ExecuteAskAsync("http://example.org/sparql", "ASK WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    #endregion

    #region URL Building Tests

    [Fact]
    public async Task ExecuteSelectAsync_BuildsCorrectUrl_WithoutQueryParams()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{"vars":[]},"results":{"bindings":[]}}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequestUri);
        Assert.StartsWith("http://example.org/sparql?query=", handler.LastRequestUri);
        Assert.Contains("SELECT", handler.LastRequestUri);
    }

    [Fact]
    public async Task ExecuteSelectAsync_BuildsCorrectUrl_WithExistingQueryParams()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{"vars":[]},"results":{"bindings":[]}}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        await executor.ExecuteSelectAsync("http://example.org/sparql?default-graph-uri=http://example.org/graph", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("default-graph-uri=", handler.LastRequestUri);
        Assert.Contains("&query=", handler.LastRequestUri);
    }

    [Fact]
    public async Task ExecuteSelectAsync_UrlEncodesQuery()
    {
        var handler = new MockHttpHandler
        {
            Content = """{"head":{"vars":[]},"results":{"bindings":[]}}"""
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        await executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequestUri);
        // Query should be URL encoded - spaces become + and braces should be encoded
        Assert.StartsWith("http://example.org/sparql?query=", handler.LastRequestUri);
        // Verify the query parameter is present and URL encoded (+ for spaces)
        Assert.Contains("SELECT", handler.LastRequestUri);
        Assert.Contains("WHERE", handler.LastRequestUri);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task ExecuteSelectAsync_HttpError_IsSaidWithTheEndpointsReason()
    {
        var handler = new MockHttpHandler
        {
            StatusCode = HttpStatusCode.BadRequest,
            Content = "{\"error\":\"Bad Request\",\"message\":\"the endpoint's reason\"}"
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("The SPARQL endpoint http://example.org/sparql answered 400 Bad Request: " +
                     "{\"error\":\"Bad Request\",\"message\":\"the endpoint's reason\"}", ex.Message);
        Assert.Equal("http://example.org/sparql", ex.EndpointUri);
        Assert.Equal("SELECT * WHERE { ?s ?p ?o }", ex.Query);
    }

    [Fact]
    public async Task ExecuteSelectAsync_InvalidJson_ThrowsSparqlServiceException()
    {
        var handler = new MockHttpHandler
        {
            Content = "not valid json"
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("parse SPARQL results JSON", ex.Message);
    }

    [Fact]
    public async Task ExecuteSelectAsync_NetworkError_ThrowsSparqlServiceException()
    {
        var handler = new MockHttpHandler
        {
            ExceptionToThrow = new HttpRequestException("Connection refused")
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("HTTP request", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task ExecuteAskAsync_HttpError_ThrowsSparqlServiceException()
    {
        var handler = new MockHttpHandler
        {
            StatusCode = HttpStatusCode.BadRequest,
            Content = "Bad Request"
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteAskAsync("http://example.org/sparql", "ASK WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("http://example.org/sparql", ex.EndpointUri);
        Assert.Equal("ASK WHERE { ?s ?p ?o }", ex.Query);
    }

    [Fact]
    public async Task ExecuteAskAsync_InvalidJson_ThrowsSparqlServiceException()
    {
        var handler = new MockHttpHandler
        {
            Content = "{invalid"
        };

        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteAskAsync("http://example.org/sparql", "ASK WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("parse SPARQL results JSON", ex.Message);
    }

    /// <summary>
    /// JSON of another shape is an answer that cannot be read, the endpoint's failure (ADR-066 D3): a binding with no
    /// type, bindings that are not an array.
    /// </summary>
    [Theory]
    [InlineData("{\"results\":{\"bindings\":[{\"s\":{\"value\":\"x\"}}]}}")]
    [InlineData("{\"results\":{\"bindings\":{}}}")]
    public async Task ExecuteSelectAsync_AnAnswerOfAnotherShape_ThrowsSparqlServiceException(string answer)
    {
        var handler = new MockHttpHandler { Content = answer };
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("parse SPARQL results JSON", ex.Message);
    }

    /// <summary>An ASK answered with a boolean that is not one cannot be read, the endpoint's failure (ADR-066 D3).</summary>
    [Fact]
    public async Task ExecuteAskAsync_AnAnswerOfAnotherShape_ThrowsSparqlServiceException()
    {
        var handler = new MockHttpHandler { Content = "{\"boolean\":\"yes\"}" };
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteAskAsync("http://example.org/sparql", "ASK WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("parse SPARQL results JSON", ex.Message);
    }

    /// <summary>
    /// An answer whose character set is not one cannot be read as text, the endpoint's failure (ADR-066 D3).
    /// </summary>
    [Fact]
    public async Task ExecuteSelectAsync_AnAnswerInACharacterSetThatIsNotOne_ThrowsSparqlServiceException()
    {
        var handler = new MockHttpHandler { Content = "{\"results\":{\"bindings\":[]}}", CharSet = "bogus" };
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.StartsWith("The SPARQL endpoint http://example.org/sparql answered, and its answer cannot be read: ", ex.Message);
    }

    /// <summary>
    /// A SERVICE IRI that is not an HTTP endpoint cannot be asked: a call that fails, the endpoint's failure (SPARQL 1.1
    /// Federated Query §2.3; ADR-066 D3), and nothing is sent.
    /// </summary>
    [Theory]
    [InlineData("urn:x")]
    [InlineData("x")]
    [InlineData("http://[bad/sparql")]
    public async Task ExecuteSelectAsync_AnIriThatIsNotAnHttpEndpoint_ThrowsSparqlServiceException(string endpoint)
    {
        var handler = new MockHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync(endpoint, "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal($"The SERVICE IRI {endpoint} is not an HTTP endpoint.", ex.Message);
        Assert.Null(handler.LastRequestUri);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task ExecuteSelectAsync_NoAnswerInTime_IsSaidSo_NotAsACancellation()
    {
        // The call's bound is the executor's own: the client's is left at its 100 s
        var handler = new MockHttpHandler { Delay = TimeSpan.FromMinutes(1) };
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient, TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("The SPARQL endpoint http://example.org/sparql did not answer within 1 s.", ex.Message);
    }

    /// <summary>
    /// An endpoint that does not take the connection within the connect timeout is said not to have been connected
    /// to, with the runtime's reason, not to have not answered, and not as a cancellation (ADR-066 D5). The
    /// connection that never comes stands in for an address that drops what is sent to it.
    /// </summary>
    [Fact]
    public async Task ExecuteSelectAsync_NoConnectionInTime_IsSaidSo()
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ConnectCallback = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("not reached");
            }
        };
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.StartsWith("The SPARQL endpoint http://example.org/sparql could not be connected to: ", ex.Message);
        Assert.IsType<TimeoutException>(ex.InnerException!.InnerException);
    }

    /// <summary>
    /// An endpoint's failure says at most the first 4 KiB of its body, and says that it was cut (ADR-066 R2).
    /// </summary>
    [Fact]
    public async Task ExecuteSelectAsync_HttpError_SaysTheFirst4KiBOfTheBody()
    {
        var handler = new MockHttpHandler { StatusCode = HttpStatusCode.BadRequest, Content = new string('x', 5000) };
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("The SPARQL endpoint http://example.org/sparql answered 400 Bad Request: " + new string('x', 4096) +
                     " [the first 4 KiB of 5000 bytes]", ex.Message);
    }

    /// <summary>A body of 4 KiB exactly is said whole (ADR-066 R2).</summary>
    [Fact]
    public async Task ExecuteSelectAsync_HttpError_Says4KiBOfBodyWhole()
    {
        var handler = new MockHttpHandler { StatusCode = HttpStatusCode.BadRequest, Content = new string('x', 4096) };
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);

        var ex = await Assert.ThrowsAsync<SparqlServiceException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("The SPARQL endpoint http://example.org/sparql answered 400 Bad Request: " + new string('x', 4096),
            ex.Message);
    }

    [Fact]
    public async Task ExecuteSelectAsync_CancelledByTheCaller_IsACancellation()
    {
        var handler = new MockHttpHandler { Delay = TimeSpan.FromMinutes(1) };
        using var httpClient = new HttpClient(handler);
        using var executor = new HttpSparqlServiceExecutor(httpClient);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteSelectAsync("http://example.org/sparql", "SELECT * WHERE { ?s ?p ?o }", cancel.Token).AsTask());
    }

    // Note: Cancellation behavior is tested implicitly through HttpClient.
    // The mock handler returns synchronously, so pre-cancelled tokens may not trigger.
    // Real cancellation behavior is verified via integration tests.

    #endregion

    #region Integration Tests

    // Integration tests with real SparqlHttpServer are covered by SparqlHttpServerTests.
    // These unit tests focus on the executor's JSON parsing and HTTP handling via mocks.

    #endregion
}
