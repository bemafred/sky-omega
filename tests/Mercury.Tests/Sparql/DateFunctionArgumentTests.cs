using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 and 1.2 §17.4.5: YEAR, MONTH, DAY, HOURS, MINUTES, SECONDS, TIMEZONE and TZ take an xsd:dateTime. Through
/// 1.8.33 they read any text with a loose reader of their own, so a plain string, an IRI or a month 13 was read as a
/// date. They now take a valid xsd:dateTime (Value.IsDateTime) and read its fields with XsdDateTime.TryRead, the reader
/// the operator mapping compares with; 24:00:00 is the next day's 00:00:00 (XML Schema 2 §3.2.7).
/// </summary>
[Collection("QuadStore")]
public class DateFunctionArgumentTests : PooledStoreTestBase
{
    public DateFunctionArgumentTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("YEAR(\"2020-03-04T05:06:07Z\")")]                                                        // a string
    [InlineData("YEAR(<http://ex/2020-03-04T05:06:07Z>)")]                                                 // an IRI
    [InlineData("YEAR(\"2020-03-04\"^^<http://www.w3.org/2001/XMLSchema#date>)")]                           // an xsd:date
    [InlineData("MONTH(\"2020-13-04T05:06:07\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)")]             // ill-typed
    [InlineData("TZ(\"2020-03-04T05:06:07+25:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)")]          // a bad zone
    [InlineData("TIMEZONE(\"2020-03-04T05:06:07\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)")]          // no zone
    public void DateFunction_OfAnythingButADateTime_IsUnbound(string call)
    {
        var result = SparqlEngine.Query(Store, $"SELECT ?v WHERE {{ BIND({call} AS ?v) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Assert.Single(result.Rows!).ContainsKey("v"));
    }

    [Theory]
    [InlineData("HOURS", "2020-02-29T24:00:00", "\"0\"^^<http://www.w3.org/2001/XMLSchema#integer>")]  // the next day's 00:00
    [InlineData("DAY", "2020-02-29T24:00:00", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("MONTH", "2020-02-29T24:00:00", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("SECONDS", "2020-03-04T05:06:07.250+01:30", "\"7.25\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]   // a computed decimal: canonical
    [InlineData("HOURS", "2020-03-04T05:06:07+01:30", "\"5\"^^<http://www.w3.org/2001/XMLSchema#integer>")]   // no zone conversion
    [InlineData("TZ", "2020-03-04T05:06:07-08:00", "\"-08:00\"")]
    [InlineData("TIMEZONE", "2020-03-04T05:06:07+01:30", "\"PT1H30M\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>")]
    public void DateFunction_ReadsTheFieldsOfAStoredDateTime(string function, string lexical, string expected)
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/when>", $"\"{lexical}\"^^<http://www.w3.org/2001/XMLSchema#dateTime>");

        var result = SparqlEngine.Query(Store, $"SELECT ?v WHERE {{ <http://ex/s> <http://ex/when> ?d BIND({function}(?d) AS ?v) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, Assert.Single(result.Rows!)["v"]);
    }
}
