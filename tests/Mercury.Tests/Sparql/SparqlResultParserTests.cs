using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Results;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for SPARQL result parsers (JSON, XML, CSV, TSV).
/// </summary>
public class SparqlResultParserTests
{
    #region JSON Parser - Basic

    [Fact]
    public async Task JsonParser_SelectResult_ParsesVariables()
    {
        var json = @"{
            ""head"": { ""vars"": [""s"", ""p"", ""o""] },
            ""results"": { ""bindings"": [] }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        Assert.Equal(3, parser.Variables.Count);
        Assert.Contains("s", parser.Variables);
        Assert.Contains("p", parser.Variables);
        Assert.Contains("o", parser.Variables);
    }

    [Fact]
    public async Task JsonParser_SelectResult_ParsesBindings()
    {
        var json = @"{
            ""head"": { ""vars"": [""name"", ""age""] },
            ""results"": {
                ""bindings"": [
                    {
                        ""name"": { ""type"": ""literal"", ""value"": ""Alice"" },
                        ""age"": { ""type"": ""literal"", ""value"": ""30"", ""datatype"": ""http://www.w3.org/2001/XMLSchema#integer"" }
                    }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        Assert.Single(parser.Rows);
        var row = parser.Rows[0];

        Assert.True(row.TryGetValue("name", out var name));
        Assert.Equal("Alice", name.Value);
        Assert.Equal(SparqlValueType.Literal, name.Type);

        Assert.True(row.TryGetValue("age", out var age));
        Assert.Equal("30", age.Value);
        Assert.Equal("http://www.w3.org/2001/XMLSchema#integer", age.Datatype);
    }

    [Fact]
    public async Task JsonParser_AskResult_ParsesBoolean()
    {
        var json = @"{
            ""head"": {},
            ""boolean"": true
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        Assert.True(parser.IsAskResult);
        Assert.True(parser.BooleanResult);
    }

    [Fact]
    public async Task JsonParser_AskResultFalse_ParsesBoolean()
    {
        var json = @"{
            ""head"": {},
            ""boolean"": false
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        Assert.True(parser.IsAskResult);
        Assert.False(parser.BooleanResult);
    }

    [Fact]
    public async Task JsonParser_Uri_ParsesCorrectly()
    {
        var json = @"{
            ""head"": { ""vars"": [""s""] },
            ""results"": {
                ""bindings"": [
                    { ""s"": { ""type"": ""uri"", ""value"": ""http://example.org/alice"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        var value = parser.Rows[0].GetValue("s");
        Assert.NotNull(value);
        Assert.Equal(SparqlValueType.Uri, value.Value.Type);
        Assert.Equal("http://example.org/alice", value.Value.Value);
        Assert.Equal("<http://example.org/alice>", value.Value.ToTermString());
    }

    [Fact]
    public async Task JsonParser_BlankNode_ParsesCorrectly()
    {
        var json = @"{
            ""head"": { ""vars"": [""s""] },
            ""results"": {
                ""bindings"": [
                    { ""s"": { ""type"": ""bnode"", ""value"": ""b0"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        var value = parser.Rows[0].GetValue("s");
        Assert.NotNull(value);
        Assert.Equal(SparqlValueType.BlankNode, value.Value.Type);
        Assert.Equal("_:b0", value.Value.ToTermString());
    }

    [Fact]
    public async Task JsonParser_LanguageTaggedLiteral_ParsesCorrectly()
    {
        var json = @"{
            ""head"": { ""vars"": [""name""] },
            ""results"": {
                ""bindings"": [
                    { ""name"": { ""type"": ""literal"", ""value"": ""Bonjour"", ""xml:lang"": ""fr"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        var value = parser.Rows[0].GetValue("name");
        Assert.NotNull(value);
        Assert.Equal("Bonjour", value.Value.Value);
        Assert.Equal("fr", value.Value.Language);
        Assert.Contains("@fr", value.Value.ToTermString());
    }

    [Fact]
    public async Task JsonParser_MultipleRows_ParsesAll()
    {
        var json = @"{
            ""head"": { ""vars"": [""name""] },
            ""results"": {
                ""bindings"": [
                    { ""name"": { ""type"": ""literal"", ""value"": ""Alice"" } },
                    { ""name"": { ""type"": ""literal"", ""value"": ""Bob"" } },
                    { ""name"": { ""type"": ""literal"", ""value"": ""Charlie"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        Assert.Equal(3, parser.Rows.Count);
    }

    [Fact]
    public async Task JsonParser_UnboundVariable_NotInRow()
    {
        var json = @"{
            ""head"": { ""vars"": [""s"", ""name""] },
            ""results"": {
                ""bindings"": [
                    { ""s"": { ""type"": ""uri"", ""value"": ""http://example.org/alice"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        var row = parser.Rows[0];
        Assert.True(row.IsBound("s"));
        Assert.False(row.IsBound("name"));
    }

    #endregion

    #region XML Parser - Basic

    [Fact]
    public async Task XmlParser_SelectResult_ParsesVariables()
    {
        var xml = @"<?xml version=""1.0""?>
            <sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
                <head>
                    <variable name=""s""/>
                    <variable name=""p""/>
                    <variable name=""o""/>
                </head>
                <results>
                </results>
            </sparql>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await using var parser = new SparqlXmlResultParser(stream);
        await parser.ParseAsync();

        Assert.Equal(3, parser.Variables.Count);
        Assert.Contains("s", parser.Variables);
    }

    [Fact]
    public async Task XmlParser_SelectResult_ParsesBindings()
    {
        var xml = @"<?xml version=""1.0""?>
            <sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
                <head>
                    <variable name=""name""/>
                </head>
                <results>
                    <result>
                        <binding name=""name"">
                            <literal>Alice</literal>
                        </binding>
                    </result>
                </results>
            </sparql>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await using var parser = new SparqlXmlResultParser(stream);
        await parser.ParseAsync();

        Assert.Single(parser.Rows);
        var value = parser.Rows[0].GetValue("name");
        Assert.NotNull(value);
        Assert.Equal("Alice", value.Value.Value);
    }

    [Fact]
    public async Task XmlParser_AskResult_ParsesBoolean()
    {
        var xml = @"<?xml version=""1.0""?>
            <sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
                <head/>
                <boolean>true</boolean>
            </sparql>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await using var parser = new SparqlXmlResultParser(stream);
        await parser.ParseAsync();

        Assert.True(parser.IsAskResult);
        Assert.True(parser.BooleanResult);
    }

    [Fact]
    public async Task XmlParser_Uri_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0""?>
            <sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
                <head><variable name=""s""/></head>
                <results>
                    <result>
                        <binding name=""s"">
                            <uri>http://example.org/alice</uri>
                        </binding>
                    </result>
                </results>
            </sparql>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await using var parser = new SparqlXmlResultParser(stream);
        await parser.ParseAsync();

        var value = parser.Rows[0].GetValue("s");
        Assert.NotNull(value);
        Assert.Equal(SparqlValueType.Uri, value.Value.Type);
        Assert.Equal("http://example.org/alice", value.Value.Value);
    }

    [Fact]
    public async Task XmlParser_BlankNode_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0""?>
            <sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
                <head><variable name=""s""/></head>
                <results>
                    <result>
                        <binding name=""s"">
                            <bnode>b0</bnode>
                        </binding>
                    </result>
                </results>
            </sparql>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await using var parser = new SparqlXmlResultParser(stream);
        await parser.ParseAsync();

        var value = parser.Rows[0].GetValue("s");
        Assert.NotNull(value);
        Assert.Equal(SparqlValueType.BlankNode, value.Value.Type);
    }

    [Fact]
    public async Task XmlParser_TypedLiteral_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0""?>
            <sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
                <head><variable name=""age""/></head>
                <results>
                    <result>
                        <binding name=""age"">
                            <literal datatype=""http://www.w3.org/2001/XMLSchema#integer"">30</literal>
                        </binding>
                    </result>
                </results>
            </sparql>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await using var parser = new SparqlXmlResultParser(stream);
        await parser.ParseAsync();

        var value = parser.Rows[0].GetValue("age");
        Assert.NotNull(value);
        Assert.Equal("30", value.Value.Value);
        Assert.Equal("http://www.w3.org/2001/XMLSchema#integer", value.Value.Datatype);
    }

    [Fact]
    public async Task XmlParser_LanguageTaggedLiteral_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0""?>
            <sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
                <head><variable name=""name""/></head>
                <results>
                    <result>
                        <binding name=""name"">
                            <literal xml:lang=""fr"">Bonjour</literal>
                        </binding>
                    </result>
                </results>
            </sparql>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await using var parser = new SparqlXmlResultParser(stream);
        await parser.ParseAsync();

        var value = parser.Rows[0].GetValue("name");
        Assert.NotNull(value);
        Assert.Equal("Bonjour", value.Value.Value);
        Assert.Equal("fr", value.Value.Language);
    }

    #endregion

    #region CSV Parser - Basic

    [Fact]
    public async Task CsvParser_ParsesVariables()
    {
        var csv = "name,age\nAlice,30";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        Assert.Equal(2, parser.Variables.Count);
        Assert.Contains("name", parser.Variables);
        Assert.Contains("age", parser.Variables);
    }

    [Fact]
    public async Task CsvParser_ParsesValues()
    {
        var csv = "name,age\nAlice,30";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        Assert.Single(parser.Rows);
        var row = parser.Rows[0];

        var name = row.GetValue("name");
        Assert.NotNull(name);
        Assert.Equal("Alice", name.Value.Value);
    }

    [Fact]
    public async Task CsvParser_QuotedValues_ParsesCorrectly()
    {
        var csv = "name,description\nAlice,\"Has, comma\"";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        var desc = parser.Rows[0].GetValue("description");
        Assert.NotNull(desc);
        Assert.Equal("Has, comma", desc.Value.Value);
    }

    [Fact]
    public async Task CsvParser_EscapedQuotes_ParsesCorrectly()
    {
        var csv = "name,quote\nAlice,\"She said \"\"Hello\"\"\"";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        var quote = parser.Rows[0].GetValue("quote");
        Assert.NotNull(quote);
        Assert.Equal("She said \"Hello\"", quote.Value.Value);
    }

    [Fact]
    public async Task CsvParser_UriHeuristic_DetectsUri()
    {
        var csv = "s\nhttp://example.org/alice";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        var s = parser.Rows[0].GetValue("s");
        Assert.NotNull(s);
        Assert.Equal(SparqlValueType.Uri, s.Value.Type);
    }

    [Fact]
    public async Task CsvParser_BlankNode_ParsesCorrectly()
    {
        var csv = "s\n_:b0";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        var s = parser.Rows[0].GetValue("s");
        Assert.NotNull(s);
        Assert.Equal(SparqlValueType.BlankNode, s.Value.Type);
    }

    [Fact]
    public async Task CsvParser_EmptyValue_NotBound()
    {
        var csv = "s,name\nhttp://example.org/alice,";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        var row = parser.Rows[0];
        Assert.True(row.IsBound("s"));
        Assert.False(row.IsBound("name"));
    }

    [Fact]
    public async Task CsvParser_MultipleRows_ParsesAll()
    {
        var csv = "name\nAlice\nBob\nCharlie";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var parser = new SparqlCsvResultParser(stream);
        await parser.ParseAsync();

        Assert.Equal(3, parser.Rows.Count);
    }

    #endregion

    #region TSV Parser - Basic

    [Fact]
    public async Task TsvParser_ParsesVariablesWithQuestionMark()
    {
        var tsv = "?name\t?age\nAlice\t30";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(tsv));
        await using var parser = new SparqlCsvResultParser(stream, isTsv: true);
        await parser.ParseAsync();

        Assert.Equal(2, parser.Variables.Count);
        Assert.Contains("name", parser.Variables);
        Assert.Contains("age", parser.Variables);
    }

    [Fact]
    public async Task TsvParser_Uri_ParsesCorrectly()
    {
        var tsv = "?s\n<http://example.org/alice>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(tsv));
        await using var parser = new SparqlCsvResultParser(stream, isTsv: true);
        await parser.ParseAsync();

        var s = parser.Rows[0].GetValue("s");
        Assert.NotNull(s);
        Assert.Equal(SparqlValueType.Uri, s.Value.Type);
        Assert.Equal("http://example.org/alice", s.Value.Value);
    }

    [Fact]
    public async Task TsvParser_TypedLiteral_ParsesCorrectly()
    {
        var tsv = "?age\n\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(tsv));
        await using var parser = new SparqlCsvResultParser(stream, isTsv: true);
        await parser.ParseAsync();

        var age = parser.Rows[0].GetValue("age");
        Assert.NotNull(age);
        Assert.Equal("30", age.Value.Value);
        Assert.Equal("http://www.w3.org/2001/XMLSchema#integer", age.Value.Datatype);
    }

    [Fact]
    public async Task TsvParser_LanguageTaggedLiteral_ParsesCorrectly()
    {
        var tsv = "?name\n\"Bonjour\"@fr";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(tsv));
        await using var parser = new SparqlCsvResultParser(stream, isTsv: true);
        await parser.ParseAsync();

        var name = parser.Rows[0].GetValue("name");
        Assert.NotNull(name);
        Assert.Equal("Bonjour", name.Value.Value);
        Assert.Equal("fr", name.Value.Language);
    }

    #endregion

    #region Enumeration

    [Fact]
    public async Task JsonParser_ForEach_EnumeratesAll()
    {
        var json = @"{
            ""head"": { ""vars"": [""name""] },
            ""results"": {
                ""bindings"": [
                    { ""name"": { ""type"": ""literal"", ""value"": ""Alice"" } },
                    { ""name"": { ""type"": ""literal"", ""value"": ""Bob"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);

        var names = new List<string>();
        parser.ForEach(row =>
        {
            var value = row.GetValue("name");
            if (value != null)
                names.Add(value.Value.Value);
        });

        Assert.Equal(2, names.Count);
        Assert.Contains("Alice", names);
        Assert.Contains("Bob", names);
    }

    [Fact]
    public async Task JsonParser_EnumerateAsync_Works()
    {
        var json = @"{
            ""head"": { ""vars"": [""name""] },
            ""results"": {
                ""bindings"": [
                    { ""name"": { ""type"": ""literal"", ""value"": ""Alice"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);

        var names = new List<string>();
        await foreach (var row in parser.EnumerateAsync())
        {
            var value = row.GetValue("name");
            if (value != null)
                names.Add(value.Value.Value);
        }

        Assert.Single(names);
        Assert.Contains("Alice", names);
    }

    #endregion

    #region Variable Access

    [Fact]
    public async Task Row_TryGetValue_WithQuestionMark_Works()
    {
        var json = @"{
            ""head"": { ""vars"": [""name""] },
            ""results"": {
                ""bindings"": [
                    { ""name"": { ""type"": ""literal"", ""value"": ""Alice"" } }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        var row = parser.Rows[0];
        Assert.True(row.TryGetValue("?name", out var value));
        Assert.Equal("Alice", value.Value);
    }

    [Fact]
    public async Task Row_BoundVariables_ReturnsAll()
    {
        var json = @"{
            ""head"": { ""vars"": [""s"", ""p"", ""o""] },
            ""results"": {
                ""bindings"": [
                    {
                        ""s"": { ""type"": ""uri"", ""value"": ""http://ex.org/s"" },
                        ""p"": { ""type"": ""uri"", ""value"": ""http://ex.org/p"" }
                    }
                ]
            }
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using var parser = new SparqlJsonResultParser(stream);
        await parser.ParseAsync();

        var row = parser.Rows[0];
        Assert.Equal(2, row.Count);
        Assert.Contains("s", row.BoundVariables);
        Assert.Contains("p", row.BoundVariables);
        Assert.DoesNotContain("o", row.BoundVariables);
    }

    #endregion
}
