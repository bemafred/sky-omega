// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using SkyOmega.Mercury.Repl;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for ReplSession - the REPL session management.
/// </summary>
public class ReplSessionTests : IDisposable
{
    private readonly string _testDir;
    private readonly QuadStore _store;
    private readonly ReplSession _session;

    public ReplSessionTests()
    {
        var tempPath = TempPath.Test("repl");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _store = new QuadStore(_testDir);
        _session = new ReplSession(_store);
    }

    public void Dispose()
    {
        _session.Dispose();
        _store.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    #region Empty/Whitespace Input

    [Fact]
    public void Execute_EmptyString_ReturnsEmpty()
    {
        var result = _session.Execute("");

        Assert.Equal(ExecutionResultKind.Empty, result.Kind);
        Assert.True(result.Success);
    }

    [Fact]
    public void Execute_WhitespaceOnly_ReturnsEmpty()
    {
        var result = _session.Execute("   \t  \n  ");

        Assert.Equal(ExecutionResultKind.Empty, result.Kind);
        Assert.True(result.Success);
    }

    [Fact]
    public void Execute_Null_ReturnsEmpty()
    {
        var result = _session.Execute(null!);

        Assert.Equal(ExecutionResultKind.Empty, result.Kind);
        Assert.True(result.Success);
    }

    #endregion

    #region PREFIX Declaration

    [Fact]
    public void Execute_PrefixDeclaration_RegistersPrefix()
    {
        var result = _session.Execute("PREFIX test: <http://test.org/>");

        Assert.Equal(ExecutionResultKind.PrefixRegistered, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("test", _session.Prefixes.Keys);
        Assert.Equal("http://test.org/", _session.Prefixes["test"]);
    }

    [Fact]
    public void Execute_PrefixDeclaration_CaseInsensitive()
    {
        var result = _session.Execute("prefix myns: <http://myns.org/>");

        Assert.Equal(ExecutionResultKind.PrefixRegistered, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("myns", _session.Prefixes.Keys);
    }

    [Fact]
    public void Execute_PrefixDeclaration_InvalidSyntax_ReturnsError()
    {
        var result = _session.Execute("PREFIX missing-iri");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.False(result.Success);
        Assert.Contains("Invalid PREFIX", result.Message);
    }

    [Fact]
    public void Execute_PrefixDeclaration_MissingClosingBracket_ReturnsError()
    {
        var result = _session.Execute("PREFIX test: <http://test.org/");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.False(result.Success);
        Assert.Contains("Missing closing", result.Message);
    }

    [Fact]
    public void Prefixes_ContainsWellKnownPrefixes()
    {
        Assert.Contains("rdf", _session.Prefixes.Keys);
        Assert.Contains("rdfs", _session.Prefixes.Keys);
        Assert.Contains("xsd", _session.Prefixes.Keys);
        Assert.Contains("owl", _session.Prefixes.Keys);
        Assert.Contains("foaf", _session.Prefixes.Keys);
        Assert.Equal("http://www.w3.org/1999/02/22-rdf-syntax-ns#", _session.Prefixes["rdf"]);
    }

    [Fact]
    public void RegisterPrefix_AddsPrefix()
    {
        _session.RegisterPrefix("custom", "http://custom.org/");

        Assert.Contains("custom", _session.Prefixes.Keys);
        Assert.Equal("http://custom.org/", _session.Prefixes["custom"]);
    }

    [Fact]
    public void ClearPrefixes_RemovesAllPrefixes()
    {
        _session.ClearPrefixes();

        Assert.Empty(_session.Prefixes);
    }

    #endregion

    #region BASE Declaration

    [Fact]
    public void Execute_BaseDeclaration_SetsBase()
    {
        var result = _session.Execute("BASE <http://example.org/>");

        Assert.Equal(ExecutionResultKind.BaseSet, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("http://example.org/", result.Message);
    }

    [Fact]
    public void Execute_BaseDeclaration_CaseInsensitive()
    {
        var result = _session.Execute("base <http://base.org/>");

        Assert.Equal(ExecutionResultKind.BaseSet, result.Kind);
        Assert.True(result.Success);
    }

    [Fact]
    public void Execute_BaseDeclaration_InvalidSyntax_ReturnsError()
    {
        var result = _session.Execute("BASE missing-brackets");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.False(result.Success);
        Assert.Contains("Invalid BASE", result.Message);
    }

    #endregion

    #region SELECT Queries

    [Fact]
    public void Execute_SelectQuery_ReturnsSelectResult()
    {
        // Add test data
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.True(result.Success);
        Assert.Equal(1, result.RowCount);
        Assert.NotNull(result.Variables);
        Assert.NotNull(result.Rows);
    }

    [Fact]
    public void Execute_SelectQuery_NoResults_ReturnsEmptyRows()
    {
        var result = _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.True(result.Success);
        Assert.Equal(0, result.RowCount);
    }

    [Fact]
    public void Execute_SelectQuery_WithExplicitVariables_ReturnsVariableNames()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute("SELECT ?s ?p WHERE { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.True(result.Success);
        Assert.NotNull(result.Rows);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Execute_SelectQuery_PrependsPrefixes()
    {
        // Register a custom prefix
        _session.RegisterPrefix("test", "http://test.org/");

        // Execute a query that uses the prefix - verify it parses successfully
        var result = _session.Execute("SELECT * WHERE { test:alice test:knows ?o }");

        // The prefix should be prepended and parsed successfully
        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.True(result.Success);
    }

    [Fact]
    public void Execute_SelectQuery_WithMatchingData_ReturnsResults()
    {
        // Use full IRIs to avoid prefix expansion complexity
        _store.AddCurrent("<http://example.org/s>", "<http://example.org/p>", "<http://example.org/o>");

        var result = _session.Execute("SELECT * WHERE { <http://example.org/s> <http://example.org/p> ?o }");

        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.True(result.Success);
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public void Execute_SelectQuery_WithAggregate_ReturnsAliasedVariable()
    {
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        _store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>");
        _store.CommitBatch();

        var result = _session.Execute("SELECT (COUNT(*) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?dummy");

        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.True(result.Success);
    }

    #endregion

    #region ASK Queries

    [Fact]
    public void Execute_AskQuery_ReturnsTrue_WhenMatchExists()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute("ASK { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Ask, result.Kind);
        Assert.True(result.Success);
        Assert.True(result.AskResult);
    }

    [Fact]
    public void Execute_AskQuery_ReturnsFalse_WhenNoMatch()
    {
        var result = _session.Execute("ASK { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Ask, result.Kind);
        Assert.True(result.Success);
        Assert.False(result.AskResult);
    }

    #endregion

    #region CONSTRUCT Queries

    [Fact]
    public void Execute_ConstructQuery_ReturnsTriples()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute("CONSTRUCT { ?s <http://ex.org/new> ?o } WHERE { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Construct, result.Kind);
        Assert.True(result.Success);
        Assert.NotNull(result.Triples);
        Assert.Single(result.Triples);
    }

    [Fact]
    public void Execute_ConstructQuery_NoMatches_ReturnsEmptyTriples()
    {
        var result = _session.Execute("CONSTRUCT { ?s <http://ex.org/new> ?o } WHERE { ?s ?p ?o }");

        Assert.Equal(ExecutionResultKind.Construct, result.Kind);
        Assert.True(result.Success);
        Assert.NotNull(result.Triples);
        Assert.Empty(result.Triples);
    }

    #endregion

    #region DESCRIBE Queries

    [Fact]
    public void Execute_DescribeQuery_ReturnsTriples()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute("DESCRIBE <http://ex.org/s>");

        Assert.Equal(ExecutionResultKind.Describe, result.Kind);
        Assert.True(result.Success);
        Assert.NotNull(result.Triples);
    }

    #endregion

    #region UPDATE Operations

    [Fact]
    public void Execute_InsertData_InsertsTriples()
    {
        var result = _session.Execute("INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }");

        Assert.Equal(ExecutionResultKind.Update, result.Kind);
        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
    }

    [Fact]
    public void Execute_DeleteData_DeletesTriples()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute("DELETE DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }");

        Assert.Equal(ExecutionResultKind.Update, result.Kind);
        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
    }

    [Fact]
    public void Execute_ClearAll_ClearsStore()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute("CLEAR ALL");

        Assert.Equal(ExecutionResultKind.Update, result.Kind);
        Assert.True(result.Success);
    }

    [Fact]
    public void Execute_InvalidUpdate_ReturnsError()
    {
        var result = _session.Execute("INSERT DATA { invalid syntax }");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.False(result.Success);
    }

    #endregion

    #region REPL Commands

    [Fact]
    public void Execute_HelpCommand_ReturnsHelp()
    {
        var result = _session.Execute(":help");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("REPL Commands", result.Message);
    }

    [Fact]
    public void Execute_HelpCommand_ShortForm()
    {
        var result = _session.Execute(":h");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("REPL Commands", result.Message);
    }

    [Fact]
    public void Execute_HelpCommand_QuestionMark()
    {
        var result = _session.Execute(":?");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("REPL Commands", result.Message);
    }

    [Fact]
    public void Execute_PrefixesCommand_ListsPrefixes()
    {
        var result = _session.Execute(":prefixes");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("rdf:", result.Message);
        Assert.Contains("rdfs:", result.Message);
    }

    [Fact]
    public void Execute_PrefixesCommand_ShortForm()
    {
        var result = _session.Execute(":p");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("Registered prefixes", result.Message);
    }

    [Fact]
    public void Execute_PrefixesCommand_NoPrefixes_ShowsMessage()
    {
        _session.ClearPrefixes();

        var result = _session.Execute(":prefixes");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Contains("No prefixes", result.Message);
    }

    [Fact]
    public void Execute_ClearCommand_ClearsHistory()
    {
        // Add some history
        _session.Execute("SELECT * WHERE { ?s ?p ?o }");
        Assert.NotEmpty(_session.History);

        var result = _session.Execute(":clear");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Empty(_session.History);
    }

    [Fact]
    public void Execute_ResetCommand_ResetsSession()
    {
        // Add custom prefix and history
        _session.RegisterPrefix("custom", "http://custom.org/");
        _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        var result = _session.Execute(":reset");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Empty(_session.History);
        // Well-known prefixes should be restored
        Assert.Contains("rdf", _session.Prefixes.Keys);
        // Custom prefix should be gone
        Assert.DoesNotContain("custom", _session.Prefixes.Keys);
    }

    [Fact]
    public void Execute_HistoryCommand_ShowsHistory()
    {
        _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        var result = _session.Execute(":history");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("Query history", result.Message);
        Assert.Contains("SELECT", result.Message);
    }

    [Fact]
    public void Execute_HistoryCommand_NoHistory_ShowsMessage()
    {
        var result = _session.Execute(":history");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Contains("No query history", result.Message);
    }

    [Fact]
    public void Execute_GraphsCommand_NoGraphs_ShowsMessage()
    {
        var result = _session.Execute(":graphs");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Contains("No named graphs", result.Message);
    }

    [Fact]
    public void Execute_GraphsCommand_WithGraphs_ListsGraphs()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/graph1>");

        var result = _session.Execute(":graphs");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Contains("Named graphs", result.Message);
        Assert.Contains("http://ex.org/graph1", result.Message);
    }

    [Fact]
    public void Execute_CountCommand_CountsAllTriples()
    {
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        _store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>");
        _store.CommitBatch();

        var result = _session.Execute(":count");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Contains("Count:", result.Message);
    }

    [Fact]
    public void Execute_CountCommand_WithPattern_CountsMatchingTriples()
    {
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/type>", "<http://ex.org/Person>");
        _store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/type>", "<http://ex.org/Animal>");
        _store.CommitBatch();

        var result = _session.Execute(":count ?s <http://ex.org/type> <http://ex.org/Person>");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Contains("Count:", result.Message);
    }

    [Fact]
    public void Execute_StatsCommand_ShowsStatistics()
    {
        _store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var result = _session.Execute(":stats");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("Store Statistics", result.Message);
        Assert.Contains("Quads:", result.Message);
        Assert.Contains("Atoms:", result.Message);
        Assert.Contains("Write-Ahead Log", result.Message);
        Assert.Contains("Session:", result.Message);
    }

    [Fact]
    public void Execute_StatsCommand_ShortForm()
    {
        var result = _session.Execute(":s");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("Store Statistics", result.Message);
    }

    [Fact]
    public void Execute_QuitCommand_ReturnsExit()
    {
        var result = _session.Execute(":quit");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Equal("EXIT", result.Message);
    }

    [Fact]
    public void Execute_QuitCommand_ShortForm()
    {
        var result = _session.Execute(":q");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Equal("EXIT", result.Message);
    }

    [Fact]
    public void Execute_ExitCommand()
    {
        var result = _session.Execute(":exit");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.Equal("EXIT", result.Message);
    }

    [Fact]
    public void Execute_LoadCommand_NoPath_ReturnsError()
    {
        var result = _session.Execute(":load");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.Contains("Usage:", result.Message);
    }

    [Fact]
    public void Execute_LoadCommand_WithPath_ReturnsNotImplemented()
    {
        var result = _session.Execute(":load /path/to/file.ttl");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.Contains("not yet implemented", result.Message);
    }

    [Fact]
    public void Execute_UnknownCommand_ReturnsError()
    {
        var result = _session.Execute(":unknowncommand");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.False(result.Success);
        Assert.Contains("Unknown command", result.Message);
        Assert.Contains(":help", result.Message);
    }

    #endregion

    #region History Tracking

    [Fact]
    public void Execute_Query_AddsToHistory()
    {
        Assert.Empty(_session.History);

        _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        Assert.Single(_session.History);
        Assert.Contains("SELECT", _session.History[0]);
    }

    [Fact]
    public void Execute_Update_AddsToHistory()
    {
        _session.Execute("INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }");

        Assert.Single(_session.History);
        Assert.Contains("INSERT", _session.History[0]);
    }

    [Fact]
    public void Execute_Command_DoesNotAddToHistory()
    {
        _session.Execute(":help");

        Assert.Empty(_session.History);
    }

    [Fact]
    public void Execute_PrefixDeclaration_DoesNotAddToHistory()
    {
        _session.Execute("PREFIX test: <http://test.org/>");

        Assert.Empty(_session.History);
    }

    [Fact]
    public void Execute_BaseDeclaration_DoesNotAddToHistory()
    {
        _session.Execute("BASE <http://base.org/>");

        Assert.Empty(_session.History);
    }

    #endregion

    #region Reset Method

    [Fact]
    public void Reset_ClearsHistory()
    {
        _session.Execute("SELECT * WHERE { ?s ?p ?o }");
        Assert.NotEmpty(_session.History);

        _session.Reset();

        Assert.Empty(_session.History);
    }

    [Fact]
    public void Reset_RestoresWellKnownPrefixes()
    {
        _session.ClearPrefixes();
        Assert.Empty(_session.Prefixes);

        _session.Reset();

        Assert.Contains("rdf", _session.Prefixes.Keys);
        Assert.Contains("rdfs", _session.Prefixes.Keys);
    }

    [Fact]
    public void Reset_ClearsCustomPrefixes()
    {
        _session.RegisterPrefix("custom", "http://custom.org/");

        _session.Reset();

        Assert.DoesNotContain("custom", _session.Prefixes.Keys);
    }

    #endregion

    #region Timing Information

    [Fact]
    public void Execute_Query_IncludesParseTime()
    {
        var result = _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        Assert.True(result.ParseTime >= TimeSpan.Zero);
    }

    [Fact]
    public void Execute_Query_IncludesExecutionTime()
    {
        var result = _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        Assert.True(result.ExecutionTime >= TimeSpan.Zero);
    }

    [Fact]
    public void Execute_Query_TotalTimeIsSum()
    {
        var result = _session.Execute("SELECT * WHERE { ?s ?p ?o }");

        Assert.Equal(result.ParseTime + result.ExecutionTime, result.TotalTime);
    }

    #endregion
}
