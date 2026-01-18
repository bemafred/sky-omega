using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests for TurtleStreamWriter - streaming zero-GC Turtle writer.
/// </summary>
public class TurtleStreamWriterTests
{
    #region Prefix Declarations

    [Fact]
    public void WritePrefixes_StandardPrefixes_WritesDeclarations()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.WritePrefixes();

        var result = sw.ToString();
        Assert.Contains("@prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .", result);
        Assert.Contains("@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .", result);
        Assert.Contains("@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .", result);
    }

    [Fact]
    public void RegisterPrefix_CustomPrefix_WritesInDeclarations()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WritePrefixes();

        var result = sw.ToString();
        Assert.Contains("@prefix ex: <http://example.org/> .", result);
    }

    #endregion

    #region IRI Abbreviation

    [Fact]
    public void WriteTriple_WithMatchingPrefix_AbbreviatesIRI()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WriteTripleUngrouped(
            "<http://example.org/Alice>".AsSpan(),
            "<http://example.org/knows>".AsSpan(),
            "<http://example.org/Bob>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("ex:Alice ex:knows ex:Bob .\n", result);
    }

    [Fact]
    public void WriteTriple_NoMatchingPrefix_WritesFullIRI()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.WriteTripleUngrouped(
            "<http://unknown.org/Alice>".AsSpan(),
            "<http://unknown.org/knows>".AsSpan(),
            "<http://unknown.org/Bob>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://unknown.org/Alice> <http://unknown.org/knows> <http://unknown.org/Bob> .\n", result);
    }

    [Fact]
    public void WriteTriple_RdfType_AbbreviatesToA()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WriteTripleUngrouped(
            "<http://example.org/Alice>".AsSpan(),
            "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>".AsSpan(),
            "<http://example.org/Person>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("ex:Alice a ex:Person .\n", result);
    }

    #endregion

    #region Subject Grouping

    [Fact]
    public void WriteTriple_SameSubject_GroupsWithSemicolon()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");

        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Alice\"".AsSpan());
        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/age>".AsSpan(), "\"30\"".AsSpan());
        writer.Flush();

        var result = sw.ToString();
        // Should have semicolon separator, not two separate statements
        Assert.Contains("ex:Alice ex:name \"Alice\" ;\n    ex:age \"30\" .", result);
    }

    [Fact]
    public void WriteTriple_DifferentSubjects_WritesSeparateStatements()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");

        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Alice\"".AsSpan());
        writer.WriteTriple("<http://example.org/Bob>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Bob\"".AsSpan());
        writer.Flush();

        var result = sw.ToString();
        // Should have period separating subjects
        Assert.Contains("ex:Alice ex:name \"Alice\" .\n", result);
        Assert.Contains("ex:Bob ex:name \"Bob\" .", result);
    }

    [Fact]
    public void WriteTriple_ManyPredicatesForSubject_GroupsAll()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.RegisterPrefix("foaf", "http://xmlns.com/foaf/0.1/");

        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>".AsSpan(), "<http://xmlns.com/foaf/0.1/Person>".AsSpan());
        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://xmlns.com/foaf/0.1/name>".AsSpan(), "\"Alice\"".AsSpan());
        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://xmlns.com/foaf/0.1/age>".AsSpan(), "\"30\"".AsSpan());
        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://xmlns.com/foaf/0.1/knows>".AsSpan(), "<http://example.org/Bob>".AsSpan());
        writer.Flush();

        var result = sw.ToString();
        // All predicates should be grouped under one subject
        Assert.Contains("ex:Alice a foaf:Person", result);
        Assert.Contains(";\n    foaf:name \"Alice\"", result);
        Assert.Contains(";\n    foaf:age \"30\"", result);
        Assert.Contains(";\n    foaf:knows ex:Bob", result);
    }

    #endregion

    #region Literals

    [Fact]
    public void WriteTriple_SimpleLiteral_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WriteTripleUngrouped(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"Hello World\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("ex:s ex:p \"Hello World\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithLanguageTag_PreservesTag()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WriteTripleUngrouped(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/label>".AsSpan(),
            "\"Bonjour\"@fr".AsSpan());

        var result = sw.ToString();
        Assert.Equal("ex:s ex:label \"Bonjour\"@fr .\n", result);
    }

    [Fact]
    public void WriteTriple_TypedLiteral_PreservesDatatype()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WriteTripleUngrouped(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/count>".AsSpan(),
            "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());

        var result = sw.ToString();
        // Note: datatype IRI could be abbreviated if xsd prefix is used
        Assert.Contains("ex:s ex:count \"42\"^^", result);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public void WriteTriple_BlankNodeSubject_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WriteTripleUngrouped(
            "_:b0".AsSpan(),
            "<http://example.org/name>".AsSpan(),
            "\"Anonymous\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("_:b0 ex:name \"Anonymous\" .\n", result);
    }

    [Fact]
    public void WriteTriple_BlankNodeObject_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WriteTripleUngrouped(
            "<http://example.org/Alice>".AsSpan(),
            "<http://example.org/address>".AsSpan(),
            "_:addr1".AsSpan());

        var result = sw.ToString();
        Assert.Equal("ex:Alice ex:address _:addr1 .\n", result);
    }

    #endregion

    #region Async Writing

    [Fact]
    public async Task WriteTripleAsync_BasicTriple_WritesCorrectly()
    {
        using var sw = new StringWriter();
        await using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        await writer.WriteTripleAsync(
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>");
        await writer.FlushAsync();

        var result = sw.ToString();
        Assert.Contains("ex:s ex:p ex:o", result);
    }

    [Fact]
    public async Task WritePrefixesAsync_WritesAllPrefixes()
    {
        using var sw = new StringWriter();
        await using var writer = new TurtleStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        await writer.WritePrefixesAsync();

        var result = sw.ToString();
        Assert.Contains("@prefix ex:", result);
        Assert.Contains("@prefix rdf:", result);
    }

    #endregion

    #region Roundtrip Tests

    [Fact]
    public async Task Roundtrip_WriteAndParse_ProducesSameTripleCount()
    {
        // Write some triples with ungrouped mode (simpler format)
        using var sw = new StringWriter();
        using (var writer = new TurtleStreamWriter(sw))
        {
            // Don't use prefixes for simpler roundtrip
            writer.WriteTripleUngrouped("<http://example.org/Alice>".AsSpan(), "<http://example.org/type>".AsSpan(), "<http://example.org/Person>".AsSpan());
            writer.WriteTripleUngrouped("<http://example.org/Alice>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Alice\"".AsSpan());
            writer.WriteTripleUngrouped("<http://example.org/Alice>".AsSpan(), "<http://example.org/knows>".AsSpan(), "<http://example.org/Bob>".AsSpan());
        }

        var turtle = sw.ToString();

        // Parse it back
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        var parser = new TurtleStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        // Verify triple count matches
        Assert.Equal(3, count);
    }

    #endregion

    #region File Stream Tests

    [Fact]
    public void WriteTriple_ToFileStream_WritesCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write to file
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            using (var tw = new StreamWriter(fs, Encoding.UTF8))
            using (var writer = new TurtleStreamWriter(tw))
            {
                writer.RegisterPrefix("ex", "http://example.org/");
                writer.WritePrefixes();
                writer.WriteTriple("<http://example.org/s>".AsSpan(), "<http://example.org/p>".AsSpan(), "<http://example.org/o>".AsSpan());
            }

            // Read back
            var content = File.ReadAllText(tempFile);
            Assert.Contains("@prefix ex:", content);
            Assert.Contains("ex:s ex:p ex:o", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion
}
