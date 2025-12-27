using System;

namespace SkyOmega.Mercury.SparqlEngine.Tests;

/// <summary>
/// Tests for SPARQL parser and filter evaluator
/// </summary>
public static class Tests
{
    public static void RunAllTests()
    {
        Console.WriteLine("Running Comprehensive Tests");
        Console.WriteLine("============================");
        Console.WriteLine();

        var passed = 0;
        var failed = 0;

        RunTest("Parser: Basic SELECT query", Test_Parser_BasicSelect, ref passed, ref failed);
        RunTest("Parser: SELECT DISTINCT", Test_Parser_SelectDistinct, ref passed, ref failed);
        RunTest("Parser: PREFIX declarations", Test_Parser_PrefixDecl, ref passed, ref failed);
        RunTest("Parser: CONSTRUCT query", Test_Parser_Construct, ref passed, ref failed);
        RunTest("Parser: ASK query", Test_Parser_Ask, ref passed, ref failed);
        RunTest("Filter: Numeric comparison", Test_Filter_NumericComparison, ref passed, ref failed);
        RunTest("Filter: String comparison", Test_Filter_StringComparison, ref passed, ref failed);
        RunTest("Filter: Boolean logic", Test_Filter_BooleanLogic, ref passed, ref failed);

        Console.WriteLine();
        Console.WriteLine($"Tests Passed: {passed}");
        Console.WriteLine($"Tests Failed: {failed}");
        Console.WriteLine($"Success Rate: {(passed * 100.0 / (passed + failed)):F1}%");
    }

    private static void RunTest(string name, Func<bool> test, ref int passed, ref int failed)
    {
        Console.Write($"{name}... ");
        try
        {
            if (test())
            {
                Console.WriteLine("✓ PASS");
                passed++;
            }
            else
            {
                Console.WriteLine("✗ FAIL");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ FAIL ({ex.Message})");
            failed++;
        }
    }

    // ===== Parser Tests =====

    private static bool Test_Parser_BasicSelect()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        return result.Type == QueryType.Select &&
               result.SelectClause.SelectAll;
    }

    private static bool Test_Parser_SelectDistinct()
    {
        var query = "SELECT DISTINCT ?x WHERE { ?x ?y ?z }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        return result.Type == QueryType.Select &&
               result.SelectClause.Distinct &&
               !result.SelectClause.SelectAll;
    }

    private static bool Test_Parser_PrefixDecl()
    {
        var query = "PREFIX foaf: <http://xmlns.com/foaf/0.1/> SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        return result.Type == QueryType.Select;
    }

    private static bool Test_Parser_Construct()
    {
        var query = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        return result.Type == QueryType.Construct;
    }

    private static bool Test_Parser_Ask()
    {
        var query = "ASK { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        return result.Type == QueryType.Ask;
    }

    // ===== Filter Tests =====

    private static bool Test_Filter_NumericComparison()
    {
        var filter = "5 > 3";
        var evaluator = new FilterEvaluator(filter.AsSpan());

        Span<Binding> bindingStorage = stackalloc Binding[16];
        var bindings = new BindingTable(bindingStorage);

        return evaluator.Evaluate(bindings);
    }

    private static bool Test_Filter_StringComparison()
    {
        var filter = "\"abc\" == \"abc\"";
        var evaluator = new FilterEvaluator(filter.AsSpan());

        Span<Binding> bindingStorage = stackalloc Binding[16];
        var bindings = new BindingTable(bindingStorage);

        return evaluator.Evaluate(bindings);
    }

    private static bool Test_Filter_BooleanLogic()
    {
        var filter = "bound(?x)";
        var evaluator = new FilterEvaluator(filter.AsSpan());

        Span<Binding> bindingStorage = stackalloc Binding[16];
        var bindings = new BindingTable(bindingStorage);

        // Should return false for unbound variable
        return !evaluator.Evaluate(bindings);
    }
}
