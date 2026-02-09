using System;
using System.IO;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Results;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for SPARQL result format writers (JSON, XML, CSV, TSV).
/// </summary>
public class SparqlResultWriterTests
{
    #region JSON Format Tests

    [Fact]
    public void JsonWriter_WriteHead_OutputsCorrectStructure()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteHead(["s", "p", "o"]);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\"head\":", result);
        Assert.Contains("\"vars\": [\"s\",\"p\",\"o\"]", result);
        Assert.Contains("\"results\":", result);
        Assert.Contains("\"bindings\": [", result);
    }

    [Fact]
    public void JsonWriter_WriteResult_OutputsBindings()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteHead(["s", "p"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?s".AsSpan(), "<http://example.org/Alice>".AsSpan());
        bindings.Bind("?p".AsSpan(), "<http://example.org/knows>".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\"s\":", result);
        Assert.Contains("\"type\":\"uri\"", result);
        Assert.Contains("\"value\":\"http://example.org/Alice\"", result);
    }

    [Fact]
    public void JsonWriter_WriteLiteral_OutputsCorrectType()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteHead(["name"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?name".AsSpan(), "\"Alice\"".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\"type\":\"literal\"", result);
        Assert.Contains("\"value\":\"Alice\"", result);
    }

    [Fact]
    public void JsonWriter_WriteLiteralWithLang_IncludesXmlLang()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteHead(["label"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?label".AsSpan(), "\"Bonjour\"@fr".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\"xml:lang\":\"fr\"", result);
        Assert.Contains("\"value\":\"Bonjour\"", result);
    }

    [Fact]
    public void JsonWriter_WriteTypedLiteral_IncludesDatatype()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteHead(["count"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?count".AsSpan(), "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\"datatype\":\"http://www.w3.org/2001/XMLSchema#integer\"", result);
        Assert.Contains("\"value\":\"42\"", result);
    }

    [Fact]
    public void JsonWriter_WriteBlankNode_OutputsBnode()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteHead(["node"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?node".AsSpan(), "_:b0".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\"type\":\"bnode\"", result);
        Assert.Contains("\"value\":\"b0\"", result);
    }

    [Fact]
    public void JsonWriter_WriteBooleanResult_OutputsBoolean()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteBooleanResult(true);

        var result = sw.ToString();
        Assert.Contains("\"boolean\": true", result);
        Assert.Contains("\"head\": { }", result);
    }

    [Fact]
    public void JsonWriter_MultipleResults_SeparatedByComma()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlJsonResultWriter(sw);

        writer.WriteHead(["s"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?s".AsSpan(), "<http://example.org/Alice>".AsSpan());
        writer.WriteResult(ref bindings);

        bindings.Clear();
        bindings.Bind("?s".AsSpan(), "<http://example.org/Bob>".AsSpan());
        writer.WriteResult(ref bindings);

        writer.WriteEnd();

        var result = sw.ToString();
        // Count occurrences of result objects
        Assert.Contains("\"value\":\"http://example.org/Alice\"", result);
        Assert.Contains("\"value\":\"http://example.org/Bob\"", result);
    }

    #endregion

    #region XML Format Tests

    [Fact]
    public void XmlWriter_WriteHead_OutputsCorrectStructure()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlXmlResultWriter(sw);

        writer.WriteHead(["s", "p", "o"]);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("<?xml version=\"1.0\"?>", result);
        Assert.Contains("<sparql xmlns=\"http://www.w3.org/2005/sparql-results#\">", result);
        Assert.Contains("<head>", result);
        Assert.Contains("<variable name=\"s\"/>", result);
        Assert.Contains("<variable name=\"p\"/>", result);
        Assert.Contains("<variable name=\"o\"/>", result);
        Assert.Contains("</sparql>", result);
    }

    [Fact]
    public void XmlWriter_WriteResult_OutputsBindings()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlXmlResultWriter(sw);

        writer.WriteHead(["s"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?s".AsSpan(), "<http://example.org/Alice>".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("<result>", result);
        Assert.Contains("<binding name=\"s\">", result);
        Assert.Contains("<uri>http://example.org/Alice</uri>", result);
    }

    [Fact]
    public void XmlWriter_WriteLiteralWithLang_IncludesXmlLang()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlXmlResultWriter(sw);

        writer.WriteHead(["label"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?label".AsSpan(), "\"Bonjour\"@fr".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("xml:lang=\"fr\"", result);
        Assert.Contains(">Bonjour</literal>", result);
    }

    [Fact]
    public void XmlWriter_WriteTypedLiteral_IncludesDatatype()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlXmlResultWriter(sw);

        writer.WriteHead(["count"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?count".AsSpan(), "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("datatype=\"http://www.w3.org/2001/XMLSchema#integer\"", result);
        Assert.Contains(">42</literal>", result);
    }

    [Fact]
    public void XmlWriter_WriteBlankNode_OutputsBnode()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlXmlResultWriter(sw);

        writer.WriteHead(["node"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?node".AsSpan(), "_:b0".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("<bnode>b0</bnode>", result);
    }

    [Fact]
    public void XmlWriter_WriteBooleanResult_OutputsBoolean()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlXmlResultWriter(sw);

        writer.WriteBooleanResult(true);

        var result = sw.ToString();
        Assert.Contains("<boolean>true</boolean>", result);
        Assert.Contains("<head/>", result);
    }

    #endregion

    #region CSV Format Tests

    [Fact]
    public void CsvWriter_WriteHead_OutputsHeaderRow()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw);

        writer.WriteHead(["s", "p", "o"]);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.StartsWith("s,p,o\n", result);
    }

    [Fact]
    public void CsvWriter_WriteResult_OutputsValues()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw);

        writer.WriteHead(["s", "p"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?s".AsSpan(), "<http://example.org/Alice>".AsSpan());
        bindings.Bind("?p".AsSpan(), "<http://example.org/knows>".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("http://example.org/Alice,http://example.org/knows", result);
    }

    [Fact]
    public void CsvWriter_WriteLiteral_ExtractsValue()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw);

        writer.WriteHead(["name"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?name".AsSpan(), "\"Alice\"".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("Alice", lines[1]);
    }

    [Fact]
    public void CsvWriter_ValueWithComma_IsQuoted()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw);

        writer.WriteHead(["value"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?value".AsSpan(), "\"Hello, World\"".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\"Hello, World\"", result);
    }

    [Fact]
    public void CsvWriter_UnboundVariable_EmptyField()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw);

        writer.WriteHead(["s", "p", "o"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?s".AsSpan(), "<http://example.org/Alice>".AsSpan());
        // ?p and ?o are unbound

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Second line should have two commas with empty fields after
        Assert.Equal("http://example.org/Alice,,", lines[1]);
    }

    #endregion

    #region TSV Format Tests

    [Fact]
    public void TsvWriter_WriteHead_OutputsHeaderRowWithQuestionMark()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw, useTsv: true);

        writer.WriteHead(["s", "p", "o"]);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.StartsWith("?s\t?p\t?o\n", result);
    }

    [Fact]
    public void TsvWriter_WriteResult_KeepsBrackets()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw, useTsv: true);

        writer.WriteHead(["s"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?s".AsSpan(), "<http://example.org/Alice>".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("<http://example.org/Alice>", result);
    }

    [Fact]
    public void TsvWriter_EscapesTabs()
    {
        using var sw = new StringWriter();
        using var writer = new SparqlCsvResultWriter(sw, useTsv: true);

        writer.WriteHead(["value"]);

        Span<Binding> bindingStorage = stackalloc Binding[8];
        Span<char> stringBuffer = stackalloc char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);

        bindings.Bind("?value".AsSpan(), "\"Hello\tWorld\"".AsSpan());

        writer.WriteResult(ref bindings);
        writer.WriteEnd();

        var result = sw.ToString();
        Assert.Contains("\\t", result);
    }

    #endregion
}
