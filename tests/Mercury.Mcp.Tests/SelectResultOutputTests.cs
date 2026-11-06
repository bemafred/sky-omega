using System.Text;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Results;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Mcp.Tests;

/// <summary>
/// mercury_query returns SELECT results as SPARQL 1.1 TSV (W3C Recommendation 2013-03-21, section 4). It used to
/// join the stored terms with tabs, so a line break in a literal started a new row, a tab started a new column, and
/// quotes and backslashes came out raw. Found 2026-11-06 after the CONSTRUCT output fix (24805b5e).
/// </summary>
public sealed class SelectResultOutputTests : IDisposable
{
    // Literals that need escaping in TSV, an empty literal, a typed number, an IRI, with and without a language tag.
    private const string Data =
        """
        INSERT DATA {
          <urn:test:a> <urn:test:p> "said \"hi\" to {\"success\":true}" .
          <urn:test:b> <urn:test:p> "C:\\path\\file" .
          <urn:test:c> <urn:test:p> "line one\nline two\tafter a tab\r" .
          <urn:test:d> <urn:test:p> "" .
          <urn:test:e> <urn:test:p> "a \"quoted\" word"@en .
          <urn:test:f> <urn:test:p> 42 .
          <urn:test:g> <urn:test:p> <urn:test:iri> .
        }
        """;

    // ?missing is never bound: the spec writes an empty field for it.
    private const string Query =
        "SELECT ?s ?o ?missing WHERE { ?s <urn:test:p> ?o OPTIONAL { ?s <urn:test:none> ?missing } } ORDER BY ?s";

    private const int RowCount = 7;

    private readonly string _dir;
    private readonly QuadStorePool _pool;
    private readonly MercuryTools _tools;

    public SelectResultOutputTests()
    {
        var tempPath = TempPath.Test("mcp-select-output");
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

    [Fact]
    public async Task SelectResult_IsTsvThatParsesBackToTheStoredTerms()
    {
        Assert.StartsWith("OK", _tools.Update(Data));
        var ct = TestContext.Current.CancellationToken;
        var stored = SparqlEngine.Query(_pool.Active, Query, ct);
        Assert.Equal(RowCount, stored.Rows!.Count);

        var (document, countLine) = Split(_tools.Query(Query));
        Assert.Equal($"{RowCount} result(s)", countLine);

        // One header line and one line per row, each with one field per variable: no literal broke a row or a column.
        var lines = document.TrimEnd('\n').Split('\n');
        Assert.Equal("?s\t?o\t?missing", lines[0]);
        Assert.Equal(RowCount + 1, lines.Length);
        Assert.All(lines, line => Assert.Equal(3, line.Split('\t').Length));

        await using var parser = new SparqlCsvResultParser(new MemoryStream(Encoding.UTF8.GetBytes(document)), isTsv: true);
        await parser.ParseAsync(ct);

        Assert.Equal(["s", "o", "missing"], parser.Variables);
        Assert.Equal(RowCount, parser.Rows.Count);
        for (int i = 0; i < RowCount; i++)
        {
            var expected = stored.Rows[i];
            var parsed = parser.Rows[i];
            Assert.Equal(expected.Keys.Order(), parsed.BoundVariables.Order());
            foreach (var (name, term) in expected)
                Assert.Equal(term, AsStoredTerm(parsed.GetValue(name)!.Value));
        }
    }

    [Fact]
    public void SelectWithNoRows_WritesTheHeaderAndAZeroCount()
    {
        var (document, countLine) = Split(_tools.Query("SELECT ?s ?o WHERE { ?s <urn:test:none> ?o }"));

        Assert.Equal("?s\t?o\n", document);
        Assert.Equal("0 result(s)", countLine);
    }

    // The TSV document, then a blank line and the count.
    private static (string Document, string CountLine) Split(string output)
    {
        var countLine = output.LastIndexOf('\n');
        return (output[..countLine], output[(countLine + 1)..]);
    }

    // A parsed TSV term in the store's own form: an IRI in angle brackets, a literal with its lexical form unescaped.
    private static string AsStoredTerm(SparqlResultValue value) => value.Type switch
    {
        SparqlValueType.Uri => $"<{value.Value}>",
        SparqlValueType.BlankNode => $"_:{value.Value}",
        _ when value.Language is { } lang => $"\"{value.Value}\"@{lang}",
        _ when value.Datatype is { } datatype => $"\"{value.Value}\"^^<{datatype}>",
        _ => $"\"{value.Value}\"",
    };
}
