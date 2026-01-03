using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for ServiceMaterializer and ServicePatternScan.
/// </summary>
public class ServiceMaterializerTests
{

    [Fact]
    public void ServicePatternScan_IteratesOverResults()
    {
        // Arrange - create mock results
        // Note: ServiceBinding.ToRdfTerm() adds quotes for literals, so pass raw values
        var results = new List<ServiceResultRow>
        {
            CreateRow(("s", "http://ex.org/item1", ServiceBindingType.Uri),
                     ("name", "Alice", ServiceBindingType.Literal)),
            CreateRow(("s", "http://ex.org/item2", ServiceBindingType.Uri),
                     ("name", "Bob", ServiceBindingType.Literal)),
            CreateRow(("s", "http://ex.org/item3", ServiceBindingType.Uri),
                     ("name", "Charlie", ServiceBindingType.Literal))
        };

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Act - scan over results
        var scan = new ServicePatternScan(results, bindingTable);
        var count = 0;
        var collectedNames = new List<string>();

        while (scan.MoveNext(ref bindingTable))
        {
            count++;
            var nameIdx = bindingTable.FindBinding("?name".AsSpan());
            if (nameIdx >= 0)
            {
                collectedNames.Add(bindingTable.GetString(nameIdx).ToString());
            }
        }
        scan.Dispose();

        // Assert
        Assert.Equal(3, count);
        Assert.Contains("\"Alice\"", collectedNames);  // ToRdfTerm() adds quotes
        Assert.Contains("\"Bob\"", collectedNames);
        Assert.Contains("\"Charlie\"", collectedNames);
    }

    [Fact]
    public void ServicePatternScan_RespectsExistingBindings()
    {
        // Arrange - results where one matches existing binding
        // Note: ToRdfTerm() for Uri adds angle brackets
        var results = new List<ServiceResultRow>
        {
            CreateRow(("s", "http://ex.org/item1", ServiceBindingType.Uri)),
            CreateRow(("s", "http://ex.org/item2", ServiceBindingType.Uri)),
            CreateRow(("s", "http://ex.org/item3", ServiceBindingType.Uri))
        };

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Pre-bind ?s to item2 (with angle brackets as ToRdfTerm produces)
        bindingTable.Bind("?s".AsSpan(), "<http://ex.org/item2>".AsSpan());

        // Act
        var scan = new ServicePatternScan(results, bindingTable);
        var count = 0;
        var matchedSubject = "";

        while (scan.MoveNext(ref bindingTable))
        {
            count++;
            var sIdx = bindingTable.FindBinding("?s".AsSpan());
            if (sIdx >= 0)
            {
                matchedSubject = bindingTable.GetString(sIdx).ToString();
            }
        }
        scan.Dispose();

        // Assert - only one row should match
        Assert.Equal(1, count);
        Assert.Equal("<http://ex.org/item2>", matchedSubject);
    }

    [Fact]
    public void ServicePatternScan_EmptyResults()
    {
        var results = new List<ServiceResultRow>();

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        var scan = new ServicePatternScan(results, bindingTable);
        var hasResults = scan.MoveNext(ref bindingTable);
        scan.Dispose();

        Assert.False(hasResults);
    }

    [Fact]
    public void ServiceMaterializer_MaterializesSimpleQuery()
    {
        // Arrange
        var mockExecutor = new MockServiceExecutor();
        mockExecutor.AddResult("http://example.org/sparql", new List<ServiceResultRow>
        {
            CreateRow(("s", "<http://ex.org/item1>", ServiceBindingType.Uri),
                     ("p", "<http://ex.org/type>", ServiceBindingType.Uri),
                     ("o", "\"Thing\"", ServiceBindingType.Literal))
        });

        var pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");
        using var materializer = new ServiceMaterializer(mockExecutor, pool);

        // Parse a query to get a ServiceClause
        var query = "SELECT * WHERE { SERVICE <http://example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);

        // Act
        var tempStore = materializer.Materialize(serviceClause, query.AsSpan());

        // Assert - temp store should contain the SERVICE results as triples
        Assert.NotNull(tempStore);

        // Cleanup
        pool.Dispose();
    }

    [Fact]
    public void ServiceMaterializer_HandlesEmptyResults()
    {
        // Arrange
        var mockExecutor = new MockServiceExecutor();
        mockExecutor.AddResult("http://example.org/sparql", new List<ServiceResultRow>());

        var pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");
        using var materializer = new ServiceMaterializer(mockExecutor, pool);

        var query = "SELECT * WHERE { SERVICE <http://example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);

        // Act
        var tempStore = materializer.Materialize(serviceClause, query.AsSpan());

        // Assert
        Assert.NotNull(tempStore);

        pool.Dispose();
    }

    [Fact]
    public void ServiceMaterializer_HandlesSilentOnError()
    {
        // Arrange
        var mockExecutor = new MockServiceExecutor();
        mockExecutor.ThrowOnExecute("http://example.org/sparql",
            new SparqlServiceException("Connection failed"));

        var pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");
        using var materializer = new ServiceMaterializer(mockExecutor, pool);

        var query = "SELECT * WHERE { SERVICE SILENT <http://example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);

        // Act - should not throw due to SILENT
        var tempStore = materializer.Materialize(serviceClause, query.AsSpan());

        // Assert
        Assert.NotNull(tempStore);

        pool.Dispose();
    }

    [Fact]
    public void ServiceMaterializer_ThrowsOnErrorWithoutSilent()
    {
        // Arrange
        var mockExecutor = new MockServiceExecutor();
        mockExecutor.ThrowOnExecute("http://example.org/sparql",
            new SparqlServiceException("Connection failed"));

        var pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");
        using var materializer = new ServiceMaterializer(mockExecutor, pool);

        var query = "SELECT * WHERE { SERVICE <http://example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);

        // Act & Assert
        Assert.Throws<SparqlServiceException>(() =>
            materializer.Materialize(serviceClause, query.AsSpan()));

        pool.Dispose();
    }

    [Fact]
    public void ServiceStorePool_IsInitialized()
    {
        // Verify the global pool is available
        Assert.NotNull(ServiceStorePool.Instance);
    }

    private static ServiceResultRow CreateRow(params (string name, string value, ServiceBindingType type)[] bindings)
    {
        var row = new ServiceResultRow();
        foreach (var (name, value, type) in bindings)
        {
            row.AddBinding(name, new ServiceBinding(type, value));
        }
        return row;
    }

    /// <summary>
    /// Simple mock service executor for testing.
    /// </summary>
    private class MockServiceExecutor : ISparqlServiceExecutor
    {
        private readonly Dictionary<string, List<ServiceResultRow>> _results = new();
        private readonly Dictionary<string, Exception> _errors = new();

        public void AddResult(string endpoint, List<ServiceResultRow> rows)
        {
            _results[endpoint] = rows;
        }

        public void ThrowOnExecute(string endpoint, Exception ex)
        {
            _errors[endpoint] = ex;
        }

        public ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(
            string endpointUri, string query, System.Threading.CancellationToken ct = default)
        {
            if (_errors.TryGetValue(endpointUri, out var error))
            {
                throw error;
            }

            if (_results.TryGetValue(endpointUri, out var rows))
            {
                return ValueTask.FromResult(new List<ServiceResultRow>(rows));
            }

            return ValueTask.FromResult(new List<ServiceResultRow>());
        }

        public ValueTask<bool> ExecuteAskAsync(
            string endpointUri, string query, System.Threading.CancellationToken ct = default)
        {
            if (_errors.TryGetValue(endpointUri, out var error))
            {
                throw error;
            }

            return ValueTask.FromResult(_results.ContainsKey(endpointUri) && _results[endpointUri].Count > 0);
        }
    }
}
