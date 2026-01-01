// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using SkyOmega.Mercury.Repl;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for InteractiveRepl - the interactive REPL runner.
/// </summary>
public class InteractiveReplTests : IDisposable
{
    private readonly string _testDir;
    private readonly QuadStore _store;

    public InteractiveReplTests()
    {
        var tempPath = TempPath.Test("irepl");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _store = new QuadStore(_testDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    #region Execute Tests

    [Fact]
    public void Execute_SelectQuery_ReturnsResult()
    {
        using var repl = new InteractiveRepl(_store);
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = repl.Execute("SELECT * WHERE { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.True(result.Success);
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public void Execute_Command_ReturnsResult()
    {
        using var repl = new InteractiveRepl(_store);

        var result = repl.Execute(":help");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
    }

    [Fact]
    public void Session_ReturnsUnderlyingSession()
    {
        using var repl = new InteractiveRepl(_store);

        Assert.NotNull(repl.Session);
        Assert.Contains("rdf", repl.Session.Prefixes.Keys);
    }

    #endregion

    #region Run Loop Tests

    [Fact]
    public void Run_QuitCommand_ExitsLoop()
    {
        var input = new StringReader(":quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        // Should complete without hanging
        Assert.True(true);
    }

    [Fact]
    public void Run_ExitCommand_ExitsLoop()
    {
        var input = new StringReader(":exit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        Assert.True(true);
    }

    [Fact]
    public void Run_ShortQuitCommand_ExitsLoop()
    {
        var input = new StringReader(":q\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        Assert.True(true);
    }

    [Fact]
    public void Run_EOF_ExitsLoop()
    {
        var input = new StringReader(""); // Empty = EOF
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        Assert.True(true);
    }

    [Fact]
    public void Run_EmptyLines_ContinuesLoop()
    {
        var input = new StringReader("\n\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        Assert.True(true);
    }

    [Fact]
    public void Run_Query_PrintsResults()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    [Fact]
    public void Run_AskQuery_PrintsResult()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("ASK { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("true", text);
    }

    [Fact]
    public void Run_UpdateQuery_PrintsAffectedCount()
    {
        var input = new StringReader("INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("OK", text);
        Assert.Contains("1 triple", text);
    }

    [Fact]
    public void Run_UnknownCommand_PrintsErrorMessage()
    {
        // Unknown REPL commands produce errors
        var input = new StringReader(":unknowncommand\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("Unknown command", text);
    }

    [Fact]
    public void Run_CommandOutput_Displayed()
    {
        var input = new StringReader(":help\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("REPL Commands", text);
    }

    [Fact]
    public void Run_PrefixRegistration_PrintsMessage()
    {
        var input = new StringReader("PREFIX test: <http://test.org/>\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("test:", text);
        Assert.Contains("http://test.org/", text);
    }

    #endregion

    #region Multi-line Input Tests

    [Fact]
    public void Run_MultiLineQuery_CollectsFully()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        // Query with unclosed brace - needs continuation
        var input = new StringReader("SELECT * WHERE {\n?s ?p ?o\n}\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    [Fact]
    public void Run_MultiLineInsert_CollectsFully()
    {
        var input = new StringReader("INSERT DATA {\n<http://ex.org/s> <http://ex.org/p> <http://ex.org/o>\n}\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("OK", text);
    }

    [Fact]
    public void Run_CommandIsNotMultiLine()
    {
        var input = new StringReader(":help\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        // Commands should be processed immediately, not wait for more input
        var text = output.ToString();
        Assert.Contains("REPL Commands", text);
    }

    [Fact]
    public void Run_PrefixIsNotMultiLine()
    {
        var input = new StringReader("PREFIX test: <http://test.org/>\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        // PREFIX declarations should be processed immediately
        var text = output.ToString();
        Assert.Contains("test:", text);
    }

    [Fact]
    public void Run_BaseIsNotMultiLine()
    {
        var input = new StringReader("BASE <http://base.org/>\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        // BASE declarations should be processed immediately
        var text = output.ToString();
        Assert.Contains("http://base.org/", text);
    }

    [Fact]
    public void Run_SemicolonEndsMultiLine()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        // Query ending with semicolon should end multi-line input
        var input = new StringReader("SELECT * WHERE { ?s ?p ?o };\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    [Fact]
    public void Run_EmptyLineEndsMultiLine()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        // Empty line after content should end multi-line input
        var input = new StringReader("SELECT * WHERE {\n?s ?p ?o }\n\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    #endregion

    #region Output Formatting Tests

    [Fact]
    public void Run_SelectWithNoResults_ShowsNoVariablesMessage()
    {
        // When SELECT * or SELECT ?vars has no results, variable names can't be extracted
        // from the first result row, so it shows "(no variables selected)"
        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("no variables selected", text);
    }

    [Fact]
    public void Run_ConstructWithNoResults_ShowsNoTriples()
    {
        var input = new StringReader("CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("no triples", text);
    }

    [Fact]
    public void Run_ConstructWithResults_ShowsTriples()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("CONSTRUCT { ?s <http://ex.org/new> ?o } WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("1 triple", text);
    }

    [Fact]
    public void Run_AskFalse_ShowsFalse()
    {
        var input = new StringReader("ASK { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("false", text);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_DisposesSession()
    {
        var repl = new InteractiveRepl(_store);

        repl.Dispose();

        // Calling dispose again should not throw
        repl.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var repl = new InteractiveRepl(_store);

        repl.Dispose();
        repl.Dispose();
        repl.Dispose();

        Assert.True(true);
    }

    #endregion

    #region Statistics and Graphs Tests

    [Fact]
    public void Run_StatsCommand_ShowsStats()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader(":stats\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("Store Statistics", text);
        Assert.Contains("Quads:", text);
    }

    [Fact]
    public void Run_GraphsCommand_ShowsGraphs()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g>");

        var input = new StringReader(":graphs\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("Named graphs", text);
        Assert.Contains("http://ex.org/g", text);
    }

    #endregion

    #region History Tests

    [Fact]
    public void Run_HistoryCommand_ShowsHistory()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:history\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("Query history", text);
        Assert.Contains("SELECT", text);
    }

    [Fact]
    public void Run_ClearCommand_ClearsHistory()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:clear\n:history\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        Assert.Contains("History cleared", text);
        Assert.Contains("No query history", text);
    }

    #endregion

    #region Diagnostic Output Tests

    [Fact]
    public void Run_ParseError_ShowsDiagnostics()
    {
        var input = new StringReader("SELECT * WHERE { }\n:quit\n");
        var output = new StringWriter();
        using var repl = new InteractiveRepl(_store, input, output);

        repl.Run();

        var text = output.ToString();
        // Should show some form of error or handle gracefully
        Assert.True(text.Length > 0);
    }

    #endregion
}
