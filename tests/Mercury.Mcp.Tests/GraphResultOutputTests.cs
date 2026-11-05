using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Mcp.Tests;

/// <summary>
/// mercury_query returns CONSTRUCT and DESCRIBE results as N-Triples. It used to join the stored term strings with
/// spaces, so a literal holding a quote, a backslash or a line break came out as text no N-Triples parser accepts.
/// Found while rebuilding elite's store (2026-11-05): a comment containing {"success":true} could not be inserted
/// again from the tool's own output.
/// </summary>
public sealed class GraphResultOutputTests : IDisposable
{
    // Literals that need escaping in N-Triples: quotes, a backslash, a line break, a tab, with and without a
    // language tag or a datatype. The SPARQL escapes below are decoded when the data is inserted.
    private const string Data =
        """
        INSERT DATA {
          <urn:test:s> <urn:test:quote> "said \"hi\" to {\"success\":true}" .
          <urn:test:s> <urn:test:backslash> "C:\\path\\file" .
          <urn:test:s> <urn:test:lines> "line one\nline two\tafter a tab" .
          <urn:test:s> <urn:test:lang> "a \"quoted\" word"@en .
          <urn:test:s> <urn:test:typed> "x \"y\""^^<http://www.w3.org/2001/XMLSchema#string> .
        }
        """;

    private const int TripleCount = 5;

    private readonly string _dir;
    private readonly QuadStorePool _pool;
    private readonly MercuryTools _tools;

    public GraphResultOutputTests()
    {
        var tempPath = TempPath.Test("mcp-graph-output");
        tempPath.MarkOwnership();
        _dir = tempPath;
        _pool = new QuadStorePool(Path.Combine(_dir, "store"));
        _pool.EnsureActive("primary");
        _tools = new MercuryTools(_pool, new StorePathHolder(_dir));
    }

    public void Dispose()
    {
        _pool.Dispose();
        TempPath.SafeCleanup(_dir);
    }

    [Theory]
    [InlineData("CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }")]
    [InlineData("DESCRIBE <urn:test:s>")]
    public async Task GraphResult_IsNTriplesThatParsesBackToTheStoredTerms(string query)
    {
        Assert.StartsWith("OK", _tools.Update(Data));
        var ct = TestContext.Current.CancellationToken;
        var stored = SparqlEngine.Query(_pool.Active, query, ct).Triples;
        Assert.NotNull(stored);
        Assert.Equal(TripleCount, stored.Count);

        var output = _tools.Query(query);

        // The N-Triples document, then a blank line and the count.
        var countLine = output.LastIndexOf('\n');
        Assert.Equal($"{TripleCount} triple(s)", output[(countLine + 1)..]);
        var document = output[..countLine];

        var parsed = await RdfEngine.ParseTriplesAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(document)), RdfFormat.NTriples, ct: ct);

        Assert.Equal(Sorted(stored), Sorted(parsed));
    }

    private const string XsdStringSuffix = "^^<http://www.w3.org/2001/XMLSchema#string>";

    // Compared as RDF terms. The writer emits canonical N-Triples, which writes an xsd:string literal without its
    // datatype: in RDF 1.1 "x"^^xsd:string and "x" are the same term.
    private static List<(string, string, string)> Sorted(IEnumerable<(string S, string P, string O)> triples) =>
        triples.Select(t => (t.S, t.P, t.O.EndsWith(XsdStringSuffix, StringComparison.Ordinal) ? t.O[..^XsdStringSuffix.Length] : t.O))
            .OrderBy(t => t.P, StringComparer.Ordinal).ToList();
}
