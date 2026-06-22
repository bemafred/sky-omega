using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

// ADR-050 regression. Before the fix, the result-binding string buffer was a fixed 1 KB rented
// array and BindingTable.Bind silently returned (dropping the binding) on overflow — so any string
// literal whose lexical form (value + 2 quotes) exceeded 1024 chars, i.e. >= 1023 characters, vanished
// from SELECT result rows (the row came back without the ?o binding). The buffer now grows on demand;
// the binding survives at any size up to the store's MaxAtomSize. Threshold + well beyond are covered.
[Collection("QuadStore")]
public class LongLiteralRoundTripTests : PooledStoreTestBase
{
    public LongLiteralRoundTripTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData(500)]      // comfortably within the old buffer — was always fine
    [InlineData(1022)]     // last length that fit (1022 + 2 quotes == 1024)
    [InlineData(1023)]     // the exact failure threshold (1025 > 1024 → silently dropped pre-fix)
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(100_000)]  // far past any fixed buffer — exercises repeated geometric growth
    public void LongStringLiteral_SurvivesResultBinding(int length)
    {
        var subject = $"<urn:adr050-{length}>";
        var lit = new string('x', length);
        Store.AddCurrent(subject, "<urn:p>", "\"" + lit + "\"");

        var result = SparqlEngine.Query(Store, $"SELECT ?o WHERE {{ {subject} <urn:p> ?o }}");

        Assert.True(result.Success);
        Assert.NotNull(result.Rows);
        var row = Assert.Single(result.Rows!);
        Assert.True(row.ContainsKey("o"),
            $"the ?o binding for a {length}-char literal must not be silently dropped (ADR-050 buffer overflow)");

        var value = row["o"];
        var lex = value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        Assert.Equal(length, lex.Length);
        Assert.Equal(lit, lex);
    }
}
