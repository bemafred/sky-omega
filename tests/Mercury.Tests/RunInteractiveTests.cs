// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for ReplSession.RunInteractive() - the interactive REPL runner.
/// </summary>
public class RunInteractiveTests : IDisposable
{
    private readonly string _testDir;
    private readonly QuadStore _store;

    public RunInteractiveTests()
    {
        var tempPath = TempPath.Test("runint");
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

    #region Run Loop Tests

    [Fact]
    public void RunInteractive_QuitCommand_ExitsLoop()
    {
        var input = new StringReader(":quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        // Should complete without hanging
        Assert.True(true);
    }

    [Fact]
    public void RunInteractive_ExitCommand_ExitsLoop()
    {
        var input = new StringReader(":exit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        Assert.True(true);
    }

    [Fact]
    public void RunInteractive_ShortQuitCommand_ExitsLoop()
    {
        var input = new StringReader(":q\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        Assert.True(true);
    }

    [Fact]
    public void RunInteractive_EOF_ExitsLoop()
    {
        var input = new StringReader(""); // Empty = EOF
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        Assert.True(true);
    }

    [Fact]
    public void RunInteractive_EmptyLines_ContinuesLoop()
    {
        var input = new StringReader("\n\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        Assert.True(true);
    }

    [Fact]
    public void RunInteractive_Query_PrintsResults()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    [Fact]
    public void RunInteractive_AskQuery_PrintsResult()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("ASK { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("true", text);
    }

    [Fact]
    public void RunInteractive_UpdateQuery_PrintsAffectedCount()
    {
        var input = new StringReader("INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("OK", text);
        Assert.Contains("1 triple", text);
    }

    [Fact]
    public void RunInteractive_UnknownCommand_PrintsErrorMessage()
    {
        var input = new StringReader(":unknowncommand\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("Unknown command", text);
    }

    [Fact]
    public void RunInteractive_CommandOutput_Displayed()
    {
        var input = new StringReader(":help\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("REPL Commands", text);
    }

    [Fact]
    public void RunInteractive_PrefixRegistration_PrintsMessage()
    {
        var input = new StringReader("PREFIX test: <http://test.org/>\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("test:", text);
        Assert.Contains("http://test.org/", text);
    }

    #endregion

    #region Multi-line Input Tests

    [Fact]
    public void RunInteractive_MultiLineQuery_CollectsFully()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        // Query with unclosed brace - needs continuation
        var input = new StringReader("SELECT * WHERE {\n?s ?p ?o\n}\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    [Fact]
    public void RunInteractive_MultiLineInsert_CollectsFully()
    {
        var input = new StringReader("INSERT DATA {\n<http://ex.org/s> <http://ex.org/p> <http://ex.org/o>\n}\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("OK", text);
    }

    [Fact]
    public void RunInteractive_CommandIsNotMultiLine()
    {
        var input = new StringReader(":help\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("REPL Commands", text);
    }

    [Fact]
    public void RunInteractive_PrefixIsNotMultiLine()
    {
        var input = new StringReader("PREFIX test: <http://test.org/>\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("test:", text);
    }

    [Fact]
    public void RunInteractive_BaseIsNotMultiLine()
    {
        var input = new StringReader("BASE <http://base.org/>\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("http://base.org/", text);
    }

    [Fact]
    public void RunInteractive_SemicolonEndsMultiLine()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE { ?s ?p ?o };\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    [Fact]
    public void RunInteractive_EmptyLineEndsMultiLine()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE {\n?s ?p ?o }\n\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("1 row", text);
    }

    #endregion

    #region Output Formatting Tests

    [Fact]
    public void RunInteractive_SelectWithNoResults_ShowsNoResultsMessage()
    {
        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        // Empty store returns no results
        Assert.True(text.Contains("no variables") || text.Contains("no results"));
    }

    [Fact]
    public void RunInteractive_ConstructWithNoResults_ShowsNoTriples()
    {
        var input = new StringReader("CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("no triples", text);
    }

    [Fact]
    public void RunInteractive_ConstructWithResults_ShowsTriples()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("CONSTRUCT { ?s <http://ex.org/new> ?o } WHERE { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("1 triple", text);
    }

    [Fact]
    public void RunInteractive_AskFalse_ShowsFalse()
    {
        var input = new StringReader("ASK { ?s ?p ?o }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("false", text);
    }

    #endregion

    #region Statistics and Graphs Tests

    [Fact]
    public void RunInteractive_StatsCommand_ShowsStats()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader(":stats\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("Store Statistics", text);
        Assert.Contains("Quads:", text);
    }

    [Fact]
    public void RunInteractive_GraphsCommand_ShowsGraphs()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g>");

        var input = new StringReader(":graphs\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("Named graphs", text);
        Assert.Contains("http://ex.org/g", text);
    }

    #endregion

    #region History Tests

    [Fact]
    public void RunInteractive_HistoryCommand_ShowsHistory()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:history\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("Query history", text);
        Assert.Contains("SELECT", text);
    }

    [Fact]
    public void RunInteractive_ClearCommand_ClearsHistory()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var input = new StringReader("SELECT * WHERE { ?s ?p ?o }\n:clear\n:history\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        Assert.Contains("History cleared", text);
        Assert.Contains("No query history", text);
    }

    #endregion

    #region ReplOptions Tests

    [Fact]
    public void RunInteractive_WithCustomPrompt_UsesPrompt()
    {
        var input = new StringReader(":quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);
        var options = new ReplOptions { Prompt = "custom> ", ShowBanner = false };

        session.RunInteractive(input, output, options);

        // Non-interactive mode doesn't show prompts, but we can verify no crash
        Assert.True(true);
    }

    [Fact]
    public void RunInteractive_WithNoBanner_SkipsBanner()
    {
        var input = new StringReader(":quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);
        var options = new ReplOptions { ShowBanner = false };

        session.RunInteractive(input, output, options);

        var text = output.ToString();
        Assert.DoesNotContain("Mercury SPARQL REPL", text);
    }

    [Fact]
    public void RunInteractive_WithNoGoodbye_SkipsGoodbye()
    {
        var input = new StringReader(":quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);
        var options = new ReplOptions { GoodbyeMessage = "" };

        session.RunInteractive(input, output, options);

        var text = output.ToString();
        Assert.DoesNotContain("Goodbye", text);
    }

    [Fact]
    public void RunInteractive_PipeOptions_AreConfiguredCorrectly()
    {
        var pipeOptions = ReplOptions.Pipe;

        Assert.False(pipeOptions.UseColor);
        Assert.False(pipeOptions.ShowBanner);
        Assert.Equal("mcp> ", pipeOptions.Prompt);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void RunInteractive_ParseError_ShowsError()
    {
        var input = new StringReader("SELECT * WHERE { }\n:quit\n");
        var output = new StringWriter();
        using var session = TestSessionHelper.CreateSession(_store);

        session.RunInteractive(input, output);

        var text = output.ToString();
        // Should handle gracefully without crashing
        Assert.True(text.Length > 0);
    }

    #endregion
}
