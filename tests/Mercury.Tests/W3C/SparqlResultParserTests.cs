// Licensed under the MIT License.

using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Unit tests for SPARQL result parsing and comparison.
/// </summary>
public class SparqlResultParserTests
{
    private readonly ITestOutputHelper _output;

    public SparqlResultParserTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ParseXml_SimpleUriBindings()
    {
        var xml = @"<?xml version=""1.0""?>
<sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
  <head>
    <variable name=""s""/>
    <variable name=""p""/>
    <variable name=""o""/>
  </head>
  <results>
    <result>
      <binding name=""s""><uri>http://example.org/subject</uri></binding>
      <binding name=""p""><uri>http://example.org/predicate</uri></binding>
      <binding name=""o""><uri>http://example.org/object</uri></binding>
    </result>
  </results>
</sparql>";

        var result = SparqlResultParser.ParseXml(xml);

        Assert.Equal(3, result.Variables.Count);
        Assert.Contains("s", result.Variables);
        Assert.Contains("p", result.Variables);
        Assert.Contains("o", result.Variables);
        Assert.Single(result.Rows);

        var row = result.Rows[0];
        Assert.Equal(RdfTermType.Uri, row["s"].Type);
        Assert.Equal("http://example.org/subject", row["s"].Value);
        Assert.Equal("http://example.org/predicate", row["p"].Value);
        Assert.Equal("http://example.org/object", row["o"].Value);
    }

    [Fact]
    public void ParseXml_TypedLiteral()
    {
        var xml = @"<?xml version=""1.0""?>
<sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
  <head>
    <variable name=""count""/>
  </head>
  <results>
    <result>
      <binding name=""count"">
        <literal datatype=""http://www.w3.org/2001/XMLSchema#integer"">5</literal>
      </binding>
    </result>
  </results>
</sparql>";

        var result = SparqlResultParser.ParseXml(xml);

        Assert.Single(result.Rows);
        var binding = result.Rows[0]["count"];
        Assert.Equal(RdfTermType.Literal, binding.Type);
        Assert.Equal("5", binding.Value);
        Assert.Equal("http://www.w3.org/2001/XMLSchema#integer", binding.Datatype);
    }

    [Fact]
    public void ParseXml_LangTaggedLiteral()
    {
        var xml = @"<?xml version=""1.0""?>
<sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
  <head>
    <variable name=""name""/>
  </head>
  <results>
    <result>
      <binding name=""name"">
        <literal xml:lang=""en"">Hello</literal>
      </binding>
    </result>
  </results>
</sparql>";

        var result = SparqlResultParser.ParseXml(xml);

        var binding = result.Rows[0]["name"];
        Assert.Equal(RdfTermType.Literal, binding.Type);
        Assert.Equal("Hello", binding.Value);
        Assert.Equal("en", binding.Language);
    }

    [Fact]
    public void ParseXml_BlankNode()
    {
        var xml = @"<?xml version=""1.0""?>
<sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
  <head>
    <variable name=""x""/>
  </head>
  <results>
    <result>
      <binding name=""x""><bnode>b0</bnode></binding>
    </result>
  </results>
</sparql>";

        var result = SparqlResultParser.ParseXml(xml);

        var binding = result.Rows[0]["x"];
        Assert.Equal(RdfTermType.BNode, binding.Type);
        Assert.Equal("b0", binding.Value);
    }

    [Fact]
    public void ParseXml_BooleanResult()
    {
        var xml = @"<?xml version=""1.0""?>
<sparql xmlns=""http://www.w3.org/2005/sparql-results#"">
  <head/>
  <boolean>true</boolean>
</sparql>";

        var result = SparqlResultParser.ParseXml(xml);

        Assert.True(result.IsBoolean);
        Assert.True(result.BooleanResult);
    }

    [Fact]
    public void ParseJson_SimpleBindings()
    {
        var json = @"{
  ""head"": { ""vars"": [""s"", ""p""] },
  ""results"": {
    ""bindings"": [
      { ""s"": { ""type"": ""uri"", ""value"": ""http://example.org/s1"" },
        ""p"": { ""type"": ""uri"", ""value"": ""http://example.org/p1"" } }
    ]
  }
}";

        var result = SparqlResultParser.ParseJson(json);

        Assert.Equal(2, result.Variables.Count);
        Assert.Single(result.Rows);
        Assert.Equal("http://example.org/s1", result.Rows[0]["s"].Value);
    }

    [Fact]
    public void Compare_IdenticalResults_ReturnsNull()
    {
        var expected = new SparqlResultSet();
        expected.AddVariable("x");
        var row1 = new SparqlResultRow();
        row1.Set("x", SparqlBinding.Uri("http://example.org/a"));
        expected.AddRow(row1);

        var actual = new SparqlResultSet();
        actual.AddVariable("x");
        var row2 = new SparqlResultRow();
        row2.Set("x", SparqlBinding.Uri("http://example.org/a"));
        actual.AddRow(row2);

        var error = SparqlResultComparer.Compare(expected, actual);
        Assert.Null(error);
    }

    [Fact]
    public void Compare_DifferentValues_ReturnsError()
    {
        var expected = new SparqlResultSet();
        expected.AddVariable("x");
        var row1 = new SparqlResultRow();
        row1.Set("x", SparqlBinding.Uri("http://example.org/a"));
        expected.AddRow(row1);

        var actual = new SparqlResultSet();
        actual.AddVariable("x");
        var row2 = new SparqlResultRow();
        row2.Set("x", SparqlBinding.Uri("http://example.org/b"));
        actual.AddRow(row2);

        var error = SparqlResultComparer.Compare(expected, actual);
        Assert.NotNull(error);
    }

    [Fact]
    public void Compare_BlankNodeIsomorphism_Matches()
    {
        var expected = new SparqlResultSet();
        expected.AddVariable("x");
        var row1 = new SparqlResultRow();
        row1.Set("x", SparqlBinding.BNode("b0"));  // Blank node with label "b0"
        expected.AddRow(row1);

        var actual = new SparqlResultSet();
        actual.AddVariable("x");
        var row2 = new SparqlResultRow();
        row2.Set("x", SparqlBinding.BNode("genid123"));  // Different label
        actual.AddRow(row2);

        // Should match because blank node labels are local to the document
        var error = SparqlResultComparer.Compare(expected, actual);
        Assert.Null(error);
    }

    [Fact]
    public void Compare_UnorderedResultSets_Matches()
    {
        var expected = new SparqlResultSet();
        expected.AddVariable("x");

        var row1 = new SparqlResultRow();
        row1.Set("x", SparqlBinding.Uri("http://example.org/a"));
        expected.AddRow(row1);

        var row2 = new SparqlResultRow();
        row2.Set("x", SparqlBinding.Uri("http://example.org/b"));
        expected.AddRow(row2);

        var actual = new SparqlResultSet();
        actual.AddVariable("x");

        // Add in reverse order
        var actRow1 = new SparqlResultRow();
        actRow1.Set("x", SparqlBinding.Uri("http://example.org/b"));
        actual.AddRow(actRow1);

        var actRow2 = new SparqlResultRow();
        actRow2.Set("x", SparqlBinding.Uri("http://example.org/a"));
        actual.AddRow(actRow2);

        // Should match when not ordered
        var error = SparqlResultComparer.Compare(expected, actual, ordered: false);
        Assert.Null(error);
    }

    [Fact]
    public void Compare_OrderedResultSets_MismatchOnWrongOrder()
    {
        var expected = new SparqlResultSet();
        expected.AddVariable("x");

        var row1 = new SparqlResultRow();
        row1.Set("x", SparqlBinding.Uri("http://example.org/a"));
        expected.AddRow(row1);

        var row2 = new SparqlResultRow();
        row2.Set("x", SparqlBinding.Uri("http://example.org/b"));
        expected.AddRow(row2);

        var actual = new SparqlResultSet();
        actual.AddVariable("x");

        // Add in reverse order
        var actRow1 = new SparqlResultRow();
        actRow1.Set("x", SparqlBinding.Uri("http://example.org/b"));
        actual.AddRow(actRow1);

        var actRow2 = new SparqlResultRow();
        actRow2.Set("x", SparqlBinding.Uri("http://example.org/a"));
        actual.AddRow(actRow2);

        // Should NOT match when ordered
        var error = SparqlResultComparer.Compare(expected, actual, ordered: true);
        Assert.NotNull(error);
    }
}
