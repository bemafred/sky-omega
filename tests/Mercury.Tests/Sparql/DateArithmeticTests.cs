using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Date and time arithmetic: a date or dateTime plus or minus an xsd:dayTimeDuration or xsd:yearMonthDuration, and a
/// date or dateTime minus another, as XPath 3.1's operator mapping defines them (Functions and Operators 3.1 §9.7);
/// SPARQL 1.1's operator mapping has numbers only. Through 1.8.41 each was an error, so a query could not ask for what
/// was recorded in the last week: FILTER(?created >= NOW() - "P7D"^^xsd:dayTimeDuration) kept nothing. Where Functions
/// and Operators gives an example, the expected value is its result.
/// </summary>
[Collection("QuadStore")]
public class DateArithmeticTests : PooledStoreTestBase
{
    public DateArithmeticTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    // op:add-yearMonthDuration-to-dateTime, op:add-dayTimeDuration-to-dateTime
    [InlineData("\"2000-10-30T11:12:00\"^^xsd:dateTime + \"P1Y2M\"^^xsd:yearMonthDuration", "2001-12-30T11:12:00", "dateTime")]
    [InlineData("\"2000-10-30T11:12:00\"^^xsd:dateTime + \"P3DT1H15M\"^^xsd:dayTimeDuration", "2000-11-02T12:27:00", "dateTime")]
    // op:subtract-yearMonthDuration-from-dateTime, op:subtract-dayTimeDuration-from-dateTime
    [InlineData("\"2000-10-30T11:12:00\"^^xsd:dateTime - \"P1Y2M\"^^xsd:yearMonthDuration", "1999-08-30T11:12:00", "dateTime")]
    [InlineData("\"2000-10-30T11:12:00\"^^xsd:dateTime - \"P3DT1H15M\"^^xsd:dayTimeDuration", "2000-10-27T09:57:00", "dateTime")]
    // op:subtract-dateTimes
    [InlineData("\"2000-10-30T06:12:00-05:00\"^^xsd:dateTime - \"1999-11-28T09:00:00Z\"^^xsd:dateTime", "P337DT2H12M", "dayTimeDuration")]
    // op:add-yearMonthDuration-to-date, op:add-dayTimeDuration-to-date
    [InlineData("\"2000-10-30\"^^xsd:date + \"P1Y2M\"^^xsd:yearMonthDuration", "2001-12-30", "date")]
    [InlineData("\"2004-10-30Z\"^^xsd:date + \"P2DT2H30M0S\"^^xsd:dayTimeDuration", "2004-11-01Z", "date")]
    // op:subtract-yearMonthDuration-from-date: the day past the end of the month becomes its last day
    [InlineData("\"2000-02-29Z\"^^xsd:date - \"P1Y\"^^xsd:yearMonthDuration", "1999-02-28Z", "date")]
    [InlineData("\"2000-10-31-05:00\"^^xsd:date - \"P1Y1M\"^^xsd:yearMonthDuration", "1999-09-30-05:00", "date")]
    // op:subtract-dayTimeDuration-from-date: the date the instant falls on
    [InlineData("\"2000-10-30\"^^xsd:date - \"P3DT1H15M\"^^xsd:dayTimeDuration", "2000-10-26", "date")]
    // op:subtract-dates
    [InlineData("\"2000-10-15-05:00\"^^xsd:date - \"2000-10-10+02:00\"^^xsd:date", "P5DT7H", "dayTimeDuration")]
    // A duration first: + takes either order
    [InlineData("\"P1D\"^^xsd:dayTimeDuration + \"2000-10-30T00:00:00Z\"^^xsd:dateTime", "2000-10-31T00:00:00Z", "dateTime")]
    [InlineData("\"P1M\"^^xsd:yearMonthDuration + \"2000-01-31\"^^xsd:date", "2000-02-29", "date")]
    // NOW()'s form: the fraction and the timezone are kept
    [InlineData("\"2026-11-11T09:14:19.1234567Z\"^^xsd:dateTime - \"P1D\"^^xsd:dayTimeDuration", "2026-11-10T09:14:19.1234567Z", "dateTime")]
    [InlineData("\"2026-11-11T09:14:19.5Z\"^^xsd:dateTime + \"PT0.5S\"^^xsd:dayTimeDuration", "2026-11-11T09:14:20Z", "dateTime")]
    [InlineData("\"2026-11-11T12:00:00+00:00\"^^xsd:dateTime + \"PT1H\"^^xsd:dayTimeDuration", "2026-11-11T13:00:00Z", "dateTime")]
    [InlineData("\"2026-11-11T24:00:00Z\"^^xsd:dateTime + \"PT1S\"^^xsd:dayTimeDuration", "2026-11-12T00:00:01Z", "dateTime")]
    // A difference in its canonical form: no zero parts, PT0S for none, a sign before the P
    [InlineData("\"2026-11-11T12:00:00Z\"^^xsd:dateTime - \"2026-11-10T10:59:58.5Z\"^^xsd:dateTime", "P1DT1H1.5S", "dayTimeDuration")]
    [InlineData("\"2026-11-11T12:00:00Z\"^^xsd:dateTime - \"2026-11-11T12:00:00Z\"^^xsd:dateTime", "PT0S", "dayTimeDuration")]
    [InlineData("\"2026-11-10T00:00:00Z\"^^xsd:dateTime - \"2026-11-11T00:00:00Z\"^^xsd:dateTime", "-P1D", "dayTimeDuration")]
    [InlineData("\"2026-11-11T00:00:00\"^^xsd:dateTime - \"2026-11-10T00:00:00\"^^xsd:dateTime", "P1D", "dayTimeDuration")]
    public void Arithmetic_OnDatesAndDurations_IsXPaths(string expression, string lexical, string datatype)
    {
        var result = SparqlEngine.Query(Store,
            $"PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ({expression} AS ?r) WHERE {{ }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal($"\"{lexical}\"^^<http://www.w3.org/2001/XMLSchema#{datatype}>", Assert.Single(result.Rows!)["r"]);
    }

    [Theory]
    // Operands XPath has no operator for
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"2000-01-01T00:00:00Z\"^^xsd:dateTime")]
    [InlineData("\"P1D\"^^xsd:dayTimeDuration - \"2000-01-01T00:00:00Z\"^^xsd:dateTime")]
    [InlineData("\"2000-01-01\"^^xsd:date - \"2000-01-01T00:00:00Z\"^^xsd:dateTime")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime * \"P1D\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01\" + \"P1D\"^^xsd:dayTimeDuration")]
    [InlineData("1 + \"P1D\"^^xsd:dayTimeDuration")]
    // xsd:duration itself: XPath adds only its two subtypes to a date
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime - \"P1D\"^^xsd:duration")]
    // One with a timezone and one without: the difference depends on a timezone neither names
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime - \"2000-01-01T00:00:00\"^^xsd:dateTime")]
    // Past the years 1 to 9999
    [InlineData("\"9999-12-31T00:00:00Z\"^^xsd:dateTime + \"P1D\"^^xsd:dayTimeDuration")]
    [InlineData("\"0001-01-01T00:00:00Z\"^^xsd:dateTime - \"PT1S\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01\"^^xsd:date + \"P99999Y\"^^xsd:yearMonthDuration")]
    // Forms that are not of the datatype
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P1H\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"PT\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P1DT\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P1.5D\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"PT1S2M\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P1Y\"^^xsd:dayTimeDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P1D\"^^xsd:yearMonthDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P1M2Y\"^^xsd:yearMonthDuration")]
    [InlineData("\"2000-01-01T00:00:00Z\"^^xsd:dateTime + \"P1YT1H\"^^xsd:yearMonthDuration")]
    public void Arithmetic_OutsideXPathsOperators_IsAnError(string expression)
    {
        var result = SparqlEngine.Query(Store,
            $"PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ({expression} AS ?r) WHERE {{ }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Assert.Single(result.Rows!).ContainsKey("r"));
    }

    [Theory]
    // op:dayTimeDuration-less-than, op:yearMonthDuration-less-than
    [InlineData("\"PT24H\"^^xsd:dayTimeDuration = \"P1D\"^^xsd:dayTimeDuration", "true")]
    [InlineData("\"P1D\"^^xsd:dayTimeDuration < \"P2D\"^^xsd:dayTimeDuration", "true")]
    [InlineData("\"-P1D\"^^xsd:dayTimeDuration < \"PT0S\"^^xsd:dayTimeDuration", "true")]
    [InlineData("\"P1Y\"^^xsd:yearMonthDuration = \"P12M\"^^xsd:yearMonthDuration", "true")]
    [InlineData("\"P1Y\"^^xsd:yearMonthDuration > \"P11M\"^^xsd:yearMonthDuration", "true")]
    // op:duration-equal: months and seconds both equal, across the three duration types
    [InlineData("\"P0M\"^^xsd:yearMonthDuration = \"PT0S\"^^xsd:dayTimeDuration", "true")]
    [InlineData("\"P1Y\"^^xsd:yearMonthDuration = \"P365D\"^^xsd:dayTimeDuration", "false")]
    [InlineData("\"P1DT12H\"^^xsd:duration = \"PT36H\"^^xsd:dayTimeDuration", "true")]
    public void Comparison_OfDurations_IsByValue(string expression, string truth)
    {
        var result = SparqlEngine.Query(Store,
            $"PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ({expression} AS ?r) WHERE {{ }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal($"\"{truth}\"^^<http://www.w3.org/2001/XMLSchema#boolean>", Assert.Single(result.Rows!)["r"]);
    }

    [Theory]
    // Months and seconds are not ordered against each other, nor is xsd:duration itself
    [InlineData("\"P1Y\"^^xsd:yearMonthDuration < \"P365D\"^^xsd:dayTimeDuration")]
    [InlineData("\"P1D\"^^xsd:duration < \"P2D\"^^xsd:duration")]
    public void Order_OfDurationsXPathDoesNotOrder_IsAnError(string expression)
    {
        var result = SparqlEngine.Query(Store,
            $"PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ({expression} AS ?r) WHERE {{ }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Assert.Single(result.Rows!).ContainsKey("r"));
    }

    [Fact]
    public void Filter_OnNowMinusASpan_KeepsWhatWasRecordedInIt()
    {
        var update = SparqlEngine.Update(Store,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> " +
            "INSERT { <http://ex/old> <http://ex/created> ?old . <http://ex/recent> <http://ex/created> ?recent } " +
            "WHERE { BIND(NOW() - \"P10D\"^^xsd:dayTimeDuration AS ?old) BIND(NOW() - \"PT1H\"^^xsd:dayTimeDuration AS ?recent) }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> " +
            "SELECT ?s WHERE { ?s <http://ex/created> ?c FILTER(?c >= NOW() - \"P7D\"^^xsd:dayTimeDuration) }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(2, update.AffectedCount);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/recent>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void Filter_OnTheAgeOfARecord_ComparesItWithASpan()
    {
        Store.AddCurrent("<http://ex/old>", "<http://ex/created>",
            "\"2026-11-01T00:00:00Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>");
        Store.AddCurrent("<http://ex/recent>", "<http://ex/created>",
            "\"2026-11-10T00:00:00Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>");

        var result = SparqlEngine.Query(Store,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> " +
            "SELECT ?s WHERE { ?s <http://ex/created> ?c " +
            "FILTER(\"2026-11-11T00:00:00Z\"^^xsd:dateTime - ?c < \"P7D\"^^xsd:dayTimeDuration) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/recent>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void OrderBy_Durations_SortsThemByLength()
    {
        var result = SparqlEngine.Query(Store,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?d WHERE { " +
            "VALUES ?d { \"P2D\"^^xsd:dayTimeDuration \"PT1H\"^^xsd:dayTimeDuration \"P1D\"^^xsd:dayTimeDuration } } ORDER BY ?d",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
            ["\"PT1H\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>",
             "\"P1D\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>",
             "\"P2D\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>"],
            result.Rows!.Select(row => row["d"]));
    }

    [Fact]
    public void Sum_OfDurations_StaysAnError()
    {
        // SUM is op:numeric-add (SPARQL 1.1 §18.5.1.3): + on durations in an expression does not widen it
        var result = SparqlEngine.Query(Store,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT (SUM(?d) AS ?total) WHERE { " +
            "VALUES ?d { \"P1D\"^^xsd:dayTimeDuration \"P2D\"^^xsd:dayTimeDuration } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Assert.Single(result.Rows!).ContainsKey("total"));
    }
}
