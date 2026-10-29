using System;
using System.Collections.Generic;
using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// The property paths a scan cannot walk from a bound end, evaluated as SPARQL 1.1 §18.6 defines them. Through 1.8.30,
/// with both ends unbound, a grouped alternative such as (x/y)|p failed with "Undefined prefix '(<urn:'"; p?, ( P )?
/// and ^p? gave no zero-length match, or a row with one end unbound; ^( P ) gave a row with one end unbound; and a
/// bound object end of ( P )? was walked forward. An alternative is now a UNION and ^( P ) is P with its ends swapped,
/// translated when the query is parsed; a zero-or-one path is walked from every node of the graph. Each test seeds
/// the same four triples: a -x-> m -y-> o1, a -p-> o2 and a -q-> b, whose nodes are a, b, m, o1 and o2.
/// </summary>
[Collection("QuadStore")]
public class PathEndsUnboundTests : PooledStoreTestBase
{
    public PathEndsUnboundTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("?s (<urn:x>/<urn:y>)|<urn:p> ?o", new[] { "a o1", "a o2" })]
    [InlineData("?s <urn:x>|<urn:p>|<urn:q> ?o", new[] { "a b", "a m", "a o2" })]
    [InlineData("?s (<urn:x>|<urn:p>)/<urn:y> ?o", new[] { "a o1" })]
    [InlineData("?s <urn:x>|<urn:x> ?o", new[] { "a m", "a m" })]           // a Union keeps duplicates (§18.6)
    [InlineData("?s ^(<urn:x>/<urn:y>) ?o", new[] { "o1 a" })]               // X ^P Y is Y P X
    [InlineData("?s ^(<urn:x>|<urn:q>) ?o", new[] { "b a", "m a" })]
    [InlineData("?s <urn:q>? ?o", new[] { "a a", "a b", "b b", "m m", "o1 o1", "o2 o2" })]
    [InlineData("?s (<urn:x>/<urn:y>)? ?o", new[] { "a a", "a o1", "b b", "m m", "o1 o1", "o2 o2" })]
    [InlineData("?s ^<urn:q>? ?o", new[] { "a a", "b a", "b b", "m m", "o1 o1", "o2 o2" })]
    [InlineData("?s (<urn:x>|<urn:p>)? ?o", new[] { "a a", "a m", "a o2", "b b", "m m", "o1 o1", "o2 o2" })]
    public void Path_WithBothEndsUnbound_IsEveryPairItLinks(string pattern, string[] expected)
    {
        Store.AddCurrent("<urn:a>", "<urn:x>", "<urn:m>");
        Store.AddCurrent("<urn:m>", "<urn:y>", "<urn:o1>");
        Store.AddCurrent("<urn:a>", "<urn:p>", "<urn:o2>");
        Store.AddCurrent("<urn:a>", "<urn:q>", "<urn:b>");

        var result = SparqlEngine.Query(Store, $"SELECT ?s ?o WHERE {{ {pattern} }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var pairs = result.Rows!
            .Select(row => $"{row["s"].Replace("<urn:", "").Replace(">", "")} {row["o"].Replace("<urn:", "").Replace(">", "")}")
            .OrderBy(pair => pair, StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(pair => pair, StringComparer.Ordinal), pairs);
    }

    [Theory]
    [InlineData("<urn:a> (<urn:x>/<urn:y>)|<urn:p> ?v", new[] { "o1", "o2" })]
    [InlineData("?v (<urn:x>/<urn:y>)|<urn:p> <urn:o1>", new[] { "a" })]
    [InlineData("<urn:a> <urn:x>|<urn:x> ?v", new[] { "m", "m" })]       // the same Union with an end bound
    [InlineData("<urn:o1> ^(<urn:x>/<urn:y>) ?v", new[] { "a" })]
    [InlineData("?v ^(<urn:x>/<urn:y>) <urn:a>", new[] { "o1" })]
    [InlineData("?v (<urn:x>/<urn:y>)? <urn:o1>", new[] { "a", "o1" })]   // a bound object end is walked backward
    [InlineData("<urn:a> (<urn:x>/<urn:y>)? ?v", new[] { "a", "o1" })]
    [InlineData("?v <urn:q>? <urn:b>", new[] { "a", "b" })]
    [InlineData("?v <urn:q>? <urn:nowhere>", new[] { "nowhere" })]          // a constant is a term of the query (§9.3)
    [InlineData("?v <urn:q>? ?v", new[] { "a", "b", "m", "o1", "o2" })]
    public void Path_WithOneEndBound_IsWhatItReachesFromThatEnd(string pattern, string[] expected)
    {
        Store.AddCurrent("<urn:a>", "<urn:x>", "<urn:m>");
        Store.AddCurrent("<urn:m>", "<urn:y>", "<urn:o1>");
        Store.AddCurrent("<urn:a>", "<urn:p>", "<urn:o2>");
        Store.AddCurrent("<urn:a>", "<urn:q>", "<urn:b>");

        var result = SparqlEngine.Query(Store, $"SELECT ?v WHERE {{ {pattern} }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var values = result.Rows!.Select(row => row["v"].Replace("<urn:", "").Replace(">", "")).OrderBy(v => v, StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(v => v, StringComparer.Ordinal), values);
    }

    [Fact]
    public void ZeroOrOne_AVariableBoundOutsideTheGraph_DoesNotMatchItself()
    {
        // §9.3: a zero-length match pairs a node of the graph with itself; a value VALUES binds is not one
        Store.AddCurrent("<urn:a>", "<urn:q>", "<urn:b>");

        var result = SparqlEngine.Query(Store, "SELECT ?s WHERE { VALUES ?o { <urn:nowhere> } ?s <urn:q>? ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Rows!);
    }

    [Fact]
    public void Alternative_OfTwelveBranches_IsOneUnion()
    {
        // p1|…|p12 is one UNION of twelve branches, not twelve nested ones, so it is within the parser's depth limit
        Store.AddCurrent("<urn:a>", "<urn:p12>", "<urn:b>");
        var alternative = string.Join("|", Enumerable.Range(1, 12).Select(i => $"<urn:p{i}>"));

        var result = SparqlEngine.Query(Store, $"SELECT ?o WHERE {{ <urn:a> {alternative} ?o }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<urn:b>", Assert.Single(result.Rows!)["o"]);
    }
}
