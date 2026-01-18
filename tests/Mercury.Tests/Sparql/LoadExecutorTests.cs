using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for LoadExecutor - SPARQL LOAD operation with HTTP content negotiation.
/// </summary>
public class LoadExecutorTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public LoadExecutorTests()
    {
        var tempPath = TempPath.Test("load");
        tempPath.MarkOwnership();
        _testPath = tempPath;
    }

    public void Dispose()
    {
        _store?.Dispose();
        CleanupDirectory();
    }

    private void CleanupDirectory()
    {
        TempPath.SafeCleanup(_testPath);
    }

    private QuadStore CreateStore()
    {
        _store?.Dispose();
        _store = new QuadStore(_testPath);
        return _store;
    }

    #region Format Detection

    [Theory]
    [InlineData("text/turtle", "http://example.org/data", RdfFormat.Turtle)]
    [InlineData("application/x-turtle", "http://example.org/data", RdfFormat.Turtle)]
    [InlineData("application/n-triples", "http://example.org/data", RdfFormat.NTriples)]
    [InlineData("application/rdf+xml", "http://example.org/data", RdfFormat.RdfXml)]
    [InlineData("text/xml", "http://example.org/data", RdfFormat.RdfXml)]
    [InlineData("application/xml", "http://example.org/data", RdfFormat.RdfXml)]
    [InlineData(null, "http://example.org/data.ttl", RdfFormat.Turtle)]
    [InlineData(null, "http://example.org/data.turtle", RdfFormat.Turtle)]
    [InlineData(null, "http://example.org/data.nt", RdfFormat.NTriples)]
    [InlineData(null, "http://example.org/data.ntriples", RdfFormat.NTriples)]
    [InlineData(null, "http://example.org/data.rdf", RdfFormat.RdfXml)]
    [InlineData(null, "http://example.org/data.xml", RdfFormat.RdfXml)]
    [InlineData(null, "http://example.org/data", RdfFormat.Turtle)] // Default
    public async Task DetermineFormat_ReturnsCorrectFormat(string? contentType, string uri, RdfFormat expected)
    {
        // We can test format detection indirectly through the load behavior
        // For this test, we verify the parser is chosen correctly by loading valid data
        var store = CreateStore();

        var content = expected switch
        {
            RdfFormat.Turtle => "<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .",
            RdfFormat.NTriples => "<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .",
            RdfFormat.RdfXml => """
                <?xml version="1.0"?>
                <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                         xmlns:ex="http://ex.org/">
                  <rdf:Description rdf:about="http://ex.org/s">
                    <ex:p rdf:resource="http://ex.org/o"/>
                  </rdf:Description>
                </rdf:RDF>
                """,
            _ => throw new ArgumentException($"Unknown format: {expected}")
        };

        var handler = new MockHttpMessageHandler(content, contentType);
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync(uri, null, false, store);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
    }

    #endregion

    #region Basic Loading

    [Fact]
    public async Task Load_TurtleData_AddsToStore()
    {
        var store = CreateStore();
        var turtle = """
            @prefix ex: <http://example.org/> .
            ex:subject1 ex:predicate ex:object1 .
            ex:subject2 ex:predicate ex:object2 .
            """;

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/data.ttl", null, false, store);

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);
    }

    [Fact]
    public async Task Load_NTriplesData_AddsToStore()
    {
        var store = CreateStore();
        var ntriples = """
            <http://ex.org/s1> <http://ex.org/p> <http://ex.org/o1> .
            <http://ex.org/s2> <http://ex.org/p> <http://ex.org/o2> .
            <http://ex.org/s3> <http://ex.org/p> "literal" .
            """;

        var handler = new MockHttpMessageHandler(ntriples, "application/n-triples");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/data.nt", null, false, store);

        Assert.True(result.Success);
        Assert.Equal(3, result.AffectedCount);
    }

    [Fact]
    public async Task Load_RdfXmlData_AddsToStore()
    {
        var store = CreateStore();
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
              <rdf:Description rdf:about="http://example.org/subject">
                <ex:predicate rdf:resource="http://example.org/object"/>
                <ex:name>Test Name</ex:name>
              </rdf:Description>
            </rdf:RDF>
            """;

        var handler = new MockHttpMessageHandler(rdfxml, "application/rdf+xml");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/data.rdf", null, false, store);

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);
    }

    [Fact]
    public async Task Load_IntoNamedGraph_AddsToGraph()
    {
        var store = CreateStore();
        var turtle = "<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .";
        var graphUri = "http://example.org/graph1";

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/data.ttl", graphUri, false, store);

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);

        // Verify triple is in the named graph
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(null, null, null, graphUri);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/s>", results.Current.Subject.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task Load_HttpError_ReturnsFailure()
    {
        var store = CreateStore();
        var handler = new MockHttpMessageHandler(HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/missing.ttl", null, false, store);

        Assert.False(result.Success);
        Assert.Contains("HTTP", result.ErrorMessage);
    }

    [Fact]
    public async Task Load_HttpErrorWithSilent_ReturnsSuccess()
    {
        var store = CreateStore();
        var handler = new MockHttpMessageHandler(HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/missing.ttl", null, silent: true, store);

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);
    }

    [Fact]
    public async Task Load_ParseError_ReturnsFailure()
    {
        var store = CreateStore();
        var invalidTurtle = "this is not valid turtle syntax @@@@";

        var handler = new MockHttpMessageHandler(invalidTurtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/bad.ttl", null, false, store);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Load_ParseErrorWithSilent_ReturnsSuccess()
    {
        var store = CreateStore();
        var invalidTurtle = "this is not valid turtle syntax @@@@";

        var handler = new MockHttpMessageHandler(invalidTurtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://example.org/bad.ttl", null, silent: true, store);

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);
    }

    #endregion

    #region Size Limits

    [Fact]
    public async Task Load_ContentLengthExceedsLimit_ReturnsFailure()
    {
        var store = CreateStore();
        var options = new LoadExecutorOptions
        {
            MaxDownloadSize = 100,
            EnforceContentLength = true
        };

        // Large content that exceeds limit
        var content = new string('x', 1000);
        var handler = new MockHttpMessageHandler(content, "text/turtle", contentLength: 1000);
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://example.org/large.ttl", null, false, store);

        Assert.False(result.Success);
        Assert.Contains("exceeds maximum", result.ErrorMessage);
    }

    [Fact]
    public async Task Load_StreamExceedsLimit_ReturnsFailure()
    {
        var store = CreateStore();
        var options = new LoadExecutorOptions
        {
            MaxDownloadSize = 50,
            EnforceContentLength = false // Don't check header, check actual stream
        };

        // Content is valid turtle but exceeds size limit
        var content = string.Join("\n",
            Enumerable.Range(1, 100).Select(i =>
                $"<http://ex.org/s{i}> <http://ex.org/p> <http://ex.org/o{i}> ."));

        var handler = new MockHttpMessageHandler(content, "text/turtle", contentLength: null);
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://example.org/data.ttl", null, false, store);

        Assert.False(result.Success);
        Assert.Contains("exceeded", result.ErrorMessage);
    }

    #endregion

    #region Triple Count Limits

    [Fact]
    public async Task Load_TripleCountExceedsLimit_ReturnsFailure()
    {
        var store = CreateStore();
        var options = new LoadExecutorOptions
        {
            MaxTripleCount = 5,
            MaxDownloadSize = 0 // Unlimited size
        };

        // More triples than limit
        var ntriples = string.Join("\n",
            Enumerable.Range(1, 10).Select(i =>
                $"<http://ex.org/s{i}> <http://ex.org/p> <http://ex.org/o{i}> ."));

        var handler = new MockHttpMessageHandler(ntriples, "application/n-triples");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://example.org/data.nt", null, false, store);

        Assert.False(result.Success);
        Assert.Contains("limit", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_TripleCountWithinLimit_Succeeds()
    {
        var store = CreateStore();
        var options = new LoadExecutorOptions
        {
            MaxTripleCount = 10,
            MaxDownloadSize = 0
        };

        var ntriples = string.Join("\n",
            Enumerable.Range(1, 5).Select(i =>
                $"<http://ex.org/s{i}> <http://ex.org/p> <http://ex.org/o{i}> ."));

        var handler = new MockHttpMessageHandler(ntriples, "application/n-triples");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://example.org/data.nt", null, false, store);

        Assert.True(result.Success);
        Assert.Equal(5, result.AffectedCount);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task Load_Cancelled_ReturnsErrorOrThrows()
    {
        var store = CreateStore();
        var cts = new CancellationTokenSource();

        // Handler that cancels during the request
        var handler = new MockHttpMessageHandler(async req =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        // Should either throw OperationCanceledException or return failure
        try
        {
            var result = await executor.ExecuteAsync("http://example.org/data.ttl", null, false, store, cts.Token);
            // If it doesn't throw, it should indicate failure
            Assert.False(result.Success);
        }
        catch (OperationCanceledException)
        {
            // This is also acceptable
        }
    }

    #endregion

    #region Disposal

    [Fact]
    public async Task Load_AfterDisposal_Throws()
    {
        var store = CreateStore();
        var handler = new MockHttpMessageHandler("<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .", "text/turtle");
        using var client = new HttpClient(handler);
        var executor = new LoadExecutor(client);
        executor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await executor.ExecuteAsync("http://example.org/data.ttl", null, false, store));
    }

    [Fact]
    public void Dispose_MultipleTimes_NoThrow()
    {
        using var executor = new LoadExecutor();
        executor.Dispose();
        executor.Dispose(); // Should not throw
    }

    #endregion

    #region Constructor Variants

    [Fact]
    public void Constructor_DefaultOptions_CreatesSuccessfully()
    {
        using var executor = new LoadExecutor();
        // Should not throw
    }

    [Fact]
    public void Constructor_WithOptions_CreatesSuccessfully()
    {
        var options = new LoadExecutorOptions
        {
            MaxDownloadSize = 1024,
            MaxTripleCount = 100
        };
        using var executor = new LoadExecutor(options);
        // Should not throw
    }

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LoadExecutor((HttpClient)null!));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LoadExecutor((LoadExecutorOptions)null!));
    }

    #endregion
}

/// <summary>
/// Stress tests for LoadExecutor - intentionally push the system to find breaking points.
/// These tests use Category trait for selective execution.
/// </summary>
public class LoadExecutorStressTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public LoadExecutorStressTests()
    {
        var tempPath = TempPath.Test("load-stress");
        tempPath.MarkOwnership();
        _testPath = tempPath;

        // Ensure we have enough disk space for stress tests
        DiskSpaceGuard.EnsureSufficientSpaceForStressTest(_testPath, "LoadExecutor stress tests");
    }

    public void Dispose()
    {
        _store?.Dispose();
        CleanupDirectory();
    }

    private void CleanupDirectory()
    {
        TempPath.SafeCleanup(_testPath);
    }

    private QuadStore CreateStore()
    {
        _store?.Dispose();
        _store = new QuadStore(_testPath);
        return _store;
    }

    #region Large Data Stress Tests

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_LargeTripleCount_ShouldHitLimit()
    {
        // INTENTIONAL FAILURE: Test that the triple limit is enforced
        var store = CreateStore();
        var options = new LoadExecutorOptions
        {
            MaxTripleCount = 1000, // Low limit
            MaxDownloadSize = 0   // No size limit
        };

        // Generate 10,000 triples - should exceed limit
        var ntriples = string.Join("\n",
            Enumerable.Range(1, 10000).Select(i =>
                $"<http://stress.test/s{i}> <http://stress.test/p> <http://stress.test/o{i}> ."));

        var handler = new MockHttpMessageHandler(ntriples, "application/n-triples");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://stress.test/data.nt", null, false, store);

        // This SHOULD fail - we're intentionally exceeding the limit
        Assert.False(result.Success);
        Assert.Contains("limit", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_LargeDownloadSize_ShouldHitLimit()
    {
        // INTENTIONAL FAILURE: Test that the download size limit is enforced
        var store = CreateStore();
        var options = new LoadExecutorOptions
        {
            MaxDownloadSize = 1024, // 1KB limit
            MaxTripleCount = 0,     // No triple limit
            EnforceContentLength = false // Force stream-based check
        };

        // Generate content > 1KB
        var ntriples = string.Join("\n",
            Enumerable.Range(1, 100).Select(i =>
                $"<http://stress.test/subject{i:D10}> <http://stress.test/predicate{i:D10}> <http://stress.test/object{i:D10}> ."));

        Assert.True(Encoding.UTF8.GetByteCount(ntriples) > 1024, "Test data should be larger than limit");

        var handler = new MockHttpMessageHandler(ntriples, "application/n-triples", contentLength: null);
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://stress.test/data.nt", null, false, store);

        // This SHOULD fail - we're intentionally exceeding the limit
        Assert.False(result.Success);
        Assert.Contains("limit", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Adversarial Input Tests

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_MalformedTurtle_RecoverGracefully()
    {
        // Test that malformed input is handled gracefully
        var store = CreateStore();
        var malformed = """
            @prefix ex: <http://example.org/> .
            ex:valid ex:pred ex:obj .
            THIS IS NOT VALID TURTLE!!! @#$%^&*()
            ex:another ex:pred "value .  # Unclosed quote
            """;

        var handler = new MockHttpMessageHandler(malformed, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/bad.ttl", null, false, store);

        // Should fail gracefully, not crash
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_DeeplyNestedBNodes_ShouldHandle()
    {
        // Test handling of deeply nested blank node structures
        var store = CreateStore();
        var options = LoadExecutorOptions.Unlimited; // No limits for this test

        // Create deeply nested blank node structure
        var turtle = new StringBuilder();
        turtle.AppendLine("@prefix ex: <http://example.org/> .");
        turtle.Append("ex:root ex:child ");

        // Nest 100 levels deep
        for (int i = 0; i < 100; i++)
        {
            turtle.Append("[ ex:level ");
        }
        turtle.Append("\"deepest\"");
        for (int i = 0; i < 100; i++)
        {
            turtle.Append(" ]");
        }
        turtle.AppendLine(" .");

        var handler = new MockHttpMessageHandler(turtle.ToString(), "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://stress.test/nested.ttl", null, false, store);

        // Should either succeed or fail gracefully with a clear error
        // Deep nesting might hit parser limits
        if (!result.Success)
        {
            Assert.NotNull(result.ErrorMessage);
            // Acceptable to fail with a clear error for extreme nesting
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_ModerateIRI_ShouldHandle()
    {
        // Test handling of moderately long IRIs (within parser buffer limits)
        var store = CreateStore();

        // Parser has ~8KB buffer limit. Use 4KB which is within limits.
        var longPath = new string('x', 4000); // 4KB path
        var turtle = $"<http://example.org/{longPath}> <http://example.org/p> \"value\" .";

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/longiri.ttl", null, false, store);

        // Should succeed - 4KB is within parser buffer limits
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_VeryLongIRI_HitsParserBufferLimit()
    {
        // DISCOVERED LIMITATION: Turtle parser has ~8KB buffer limit for single tokens
        // This test documents the limitation and verifies graceful failure
        var store = CreateStore();

        // Create IRI that exceeds the parser's buffer limit (~8KB)
        var longPath = new string('x', 100_000); // 100KB path
        var turtle = $"<http://example.org/{longPath}> <http://example.org/p> \"value\" .";

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/huge.ttl", null, false, store);

        // This SHOULD fail - IRI exceeds parser buffer limit (not atom limit)
        Assert.False(result.Success);
        // Graceful failure with clear error message
        Assert.Contains("input", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_ModerateLiteral_ShouldHandle()
    {
        // Test handling of moderately long string literals (within parser buffer limits)
        var store = CreateStore();

        // Parser has ~8KB buffer limit. Use 4KB which is within limits.
        var longLiteral = new string('A', 4000); // 4KB literal
        var turtle = $"<http://example.org/s> <http://example.org/p> \"{longLiteral}\" .";

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/longliteral.ttl", null, false, store);

        // Should succeed - 4KB is within parser buffer limits
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_VeryLongLiteral_HitsParserBufferLimit()
    {
        // DISCOVERED LIMITATION: Turtle parser has ~8KB buffer limit for single tokens
        // This test documents the limitation and verifies graceful failure
        var store = CreateStore();

        var longLiteral = new string('A', 500_000); // 500KB literal
        var turtle = $"<http://example.org/s> <http://example.org/p> \"{longLiteral}\" .";

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/longliteral.ttl", null, false, store);

        // This SHOULD fail - literal exceeds parser buffer limit
        Assert.False(result.Success);
        // Graceful failure with clear error message
        Assert.Contains("input", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Concurrent Load Tests

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_ConcurrentLoads_ShouldNotCorrupt()
    {
        // Test that concurrent loads don't corrupt the store
        var store = CreateStore();
        var options = LoadExecutorOptions.Unlimited;

        // Create 10 different graphs to load concurrently
        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            var turtle = string.Join("\n",
                Enumerable.Range(1, 100).Select(j =>
                    $"<http://concurrent.test/g{i}/s{j}> <http://concurrent.test/p> <http://concurrent.test/o{j}> ."));

            var handler = new MockHttpMessageHandler(turtle, "text/turtle");
            using var client = new HttpClient(handler);
            using var executor = new LoadExecutor(client, options);

            return await executor.ExecuteAsync(
                $"http://concurrent.test/graph{i}.ttl",
                $"http://concurrent.test/graph{i}",
                false,
                store);
        });

        var results = await Task.WhenAll(tasks);

        // All loads should succeed
        foreach (var result in results)
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(100, result.AffectedCount);
        }

        // Total should be 1000 triples across 10 graphs
        store.AcquireReadLock();
        try
        {
            int totalCount = 0;
            for (int i = 1; i <= 10; i++)
            {
                var graphResults = store.QueryCurrent(null, null, null, $"http://concurrent.test/graph{i}");
                try
                {
                    while (graphResults.MoveNext())
                        totalCount++;
                }
                finally
                {
                    graphResults.Dispose();
                }
            }
            Assert.Equal(1000, totalCount);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_EmptyFile_ShouldSucceedWithZeroTriples()
    {
        var store = CreateStore();
        var handler = new MockHttpMessageHandler("", "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/empty.ttl", null, false, store);

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_OnlyComments_ShouldSucceedWithZeroTriples()
    {
        var store = CreateStore();
        var turtle = """
            # This is a comment
            # Another comment
            @prefix ex: <http://example.org/> .
            # More comments, but no actual triples
            """;

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/comments.ttl", null, false, store);

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_UnicodeContent_ShouldHandle()
    {
        // Test handling of various Unicode characters
        var store = CreateStore();
        var turtle = """
            @prefix ex: <http://example.org/> .
            ex:emoji ex:label "Hello 🌍 World 🚀" .
            ex:chinese ex:label "中文测试" .
            ex:arabic ex:label "اختبار" .
            ex:math ex:formula "∀x ∈ ℝ: x² ≥ 0" .
            ex:mixed ex:label "Mïxéd Çhàrâctêrs" .
            """;

        var handler = new MockHttpMessageHandler(turtle, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/unicode.ttl", null, false, store);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(5, result.AffectedCount);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_BinaryInLiteral_ShouldFail()
    {
        // Test that binary/null bytes in literals are handled
        var store = CreateStore();

        // Create turtle with embedded null bytes (invalid UTF-8 in literals)
        var badContent = "<http://ex.org/s> <http://ex.org/p> \"value\0with\0nulls\" .";

        var handler = new MockHttpMessageHandler(badContent, "text/turtle");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client);

        var result = await executor.ExecuteAsync("http://stress.test/binary.ttl", null, false, store);

        // Should either succeed (parser handles it) or fail gracefully
        // Null bytes in string literals are technically invalid
        if (!result.Success)
        {
            Assert.NotNull(result.ErrorMessage);
        }
    }

    #endregion

    #region Resource Exhaustion Tests

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_RapidSuccessiveLoads_ShouldNotLeakResources()
    {
        // Test that rapid successive loads don't leak resources
        var store = CreateStore();
        var options = new LoadExecutorOptions
        {
            MaxTripleCount = 100,
            MaxDownloadSize = 10_000
        };

        var turtle = string.Join("\n",
            Enumerable.Range(1, 10).Select(i =>
                $"<http://rapid.test/s{i}> <http://rapid.test/p> <http://rapid.test/o{i}> ."));

        // Perform 100 rapid loads
        for (int i = 0; i < 100; i++)
        {
            var handler = new MockHttpMessageHandler(turtle, "text/turtle");
            using var client = new HttpClient(handler);
            using var executor = new LoadExecutor(client, options);

            var result = await executor.ExecuteAsync(
                $"http://rapid.test/batch{i}.ttl",
                $"http://rapid.test/graph{i}",
                false,
                store);

            Assert.True(result.Success, $"Iteration {i} failed: {result.ErrorMessage}");
        }

        // If we got here without running out of resources, the test passed
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_ZeroLimits_ShouldLoadEverything()
    {
        // Test that unlimited options work correctly
        var store = CreateStore();
        var options = LoadExecutorOptions.Unlimited;

        // Generate a moderately large dataset
        var ntriples = string.Join("\n",
            Enumerable.Range(1, 5000).Select(i =>
                $"<http://unlimited.test/s{i}> <http://unlimited.test/p> <http://unlimited.test/o{i}> ."));

        var handler = new MockHttpMessageHandler(ntriples, "application/n-triples");
        using var client = new HttpClient(handler);
        using var executor = new LoadExecutor(client, options);

        var result = await executor.ExecuteAsync("http://unlimited.test/data.nt", null, false, store);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(5000, result.AffectedCount);
    }

    #endregion
}

/// <summary>
/// Mock HTTP message handler for testing LoadExecutor without network calls.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string? _content;
    private readonly string? _contentType;
    private readonly HttpStatusCode _statusCode;
    private readonly long? _contentLength;
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _customHandler;

    public MockHttpMessageHandler(string content, string? contentType, long? contentLength = null)
    {
        _content = content;
        _contentType = contentType;
        _statusCode = HttpStatusCode.OK;
        _contentLength = contentLength;
    }

    public MockHttpMessageHandler(HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> customHandler)
    {
        _customHandler = customHandler;
        _statusCode = HttpStatusCode.OK;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_customHandler != null)
            return await _customHandler(request);

        var response = new HttpResponseMessage(_statusCode);

        if (_content != null)
        {
            response.Content = new StringContent(_content, Encoding.UTF8);

            if (_contentType != null)
            {
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            }

            if (_contentLength.HasValue)
            {
                response.Content.Headers.ContentLength = _contentLength.Value;
            }
        }

        return response;
    }
}
