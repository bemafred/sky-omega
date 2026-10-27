using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for subquery aggregate support.
/// </summary>
public class SubqueryAggregateTests
{
    private readonly ITestOutputHelper _output;

    public SubqueryAggregateTests(ITestOutputHelper output)
    {
        _output = output;
    }

}
